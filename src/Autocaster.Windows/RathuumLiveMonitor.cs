using System.Diagnostics;
using Autocaster.Core;

namespace Autocaster.Windows;

public sealed class RathuumLiveMonitor : IRathuumMonitor
{
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Task _worker;
    private readonly RathuumGateOptions _captureOptions;
    private readonly Func<int>? _readWaveSize;
    private readonly RathuumScoreCache _warmScore = new();
    private RathuumLaunchTracker? _tracker;
    private TimeSpan _leaseStarted;
    private bool _allowCurrentScore;
    private string? _failure;

    public RathuumLiveMonitor(RathuumGateOptions options, Func<int>? readWaveSize = null, bool standby = false)
    {
        _captureOptions = options;
        _readWaveSize = readWaveSize;
        if (!standby)
            _tracker = CreateTracker(options);
        _worker = Task.Run(() => Watch(options, _stop.Token));
    }

    public IRathuumMonitor? TryLease(RathuumGateOptions options)
    {
        if (options.CompactHud != _captureOptions.CompactHud || options.ScanHz != _captureOptions.ScanHz ||
            options.FpsGuard != _captureOptions.FpsGuard || options.FirstKillOnly != _captureOptions.FirstKillOnly)
            return null;

        lock (_gate)
        {
            if (_tracker is not null || _failure is not null || _stop.IsCancellationRequested)
                return null;
            _leaseStarted = Now;
            _tracker = CreateTracker(options);
            _allowCurrentScore = options.StartAtCurrentScore;
            SeedFromWarmScore();
            _wake.Set();
            return new Lease(this);
        }
    }

    private void SeedFromWarmScore()
    {
        if (_tracker is { State.StartingScore: null } tracker && _warmScore.Read(_leaseStarted) is int score &&
            (score == 0 || _allowCurrentScore))
            tracker.BeginWave(_leaseStarted, score);
    }

    private RathuumLaunchTracker CreateTracker(RathuumGateOptions options) =>
        new(options.ToTimingOptions() with { WaveSize = _readWaveSize?.Invoke() ?? options.WaveSize }, _leaseStarted);

    public RathuumGateReading Read()
    {
        lock (_gate)
        {
            var tracker = _tracker!;
            RefreshWaveSize(tracker);
            TimeSpan now = Now;
            var failure = _failure ?? tracker.MissedLaunch(now);
            var launch = tracker.NextLaunch(now);
            return new(tracker.State, launch is { } scheduled ? Absolute(scheduled) : null, failure);
        }
    }

    public bool TryConsume(long expectedLaunch)
    {
        lock (_gate)
        {
            var tracker = _tracker!;
            RefreshWaveSize(tracker);
            TimeSpan now = Now;
            return _failure is null && tracker.NextLaunch(now) is { } scheduled &&
                expectedLaunch == Absolute(scheduled) && tracker.TryConsume(now);
        }
    }

    private void Observe(RathuumHudObservation observation)
    {
        lock (_gate)
        {
            _warmScore.Observe(observation.Timestamp, observation.RawScore);
            if (_tracker is not { } tracker)
                return;
            if (observation.Timestamp < _leaseStarted)
            {
                SeedFromWarmScore();
                return;
            }
            RefreshWaveSize(tracker);
            tracker.Observe(observation.Timestamp, Now, observation.Events, observation.RawScore,
                observation.VisibleExecutioners, observation.FeedUsable);
        }
    }

    private void RefreshWaveSize(RathuumLaunchTracker tracker)
    {
        if (_readWaveSize is { } read)
            tracker.SetWaveSize(read());
    }

    private async Task Watch(RathuumGateOptions options, CancellationToken cancellation)
    {
        try
        {
            using var capture = new RathuumScreenFrames(options.CompactHud);
            using var reader = new RathuumHudReader(options.CompactHud, observe: Observe);
            using var fpsGuard = options.FpsGuard ? new RathuumGameFps() : null;
            using var clock = new WindowsDeadlineClock { PrecisionMicroseconds = 0 };
            long nextCapture = _start, nextFps = _start;
            double workMs = 0;
            bool hadTarget = false;
            RathuumLaunchTracker? readerOwner = null;
            WaitHandle[] idleSignals = [_wake, cancellation.WaitHandle];
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                RathuumLaunchTracker? active;
                lock (_gate)
                    active = _tracker;
                if (readerOwner != active)
                {
                    if (active is not null && !options.FirstKillOnly)
                        reader.ResetTracking();
                    readerOwner = active;
                    nextCapture = Stopwatch.GetTimestamp();
                    workMs = 0;
                }
                if (active is null)
                {
                    double remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextCapture).TotalMilliseconds;
                    if (remaining > 0 && WaitHandle.WaitAny(idleSignals, (int)Math.Ceiling(remaining)) != WaitHandle.WaitTimeout)
                        continue;
                }
                else
                    clock.WaitUntil(nextCapture, cancellation);

                long began = Stopwatch.GetTimestamp();
                int cap = active is null ? 15 : capture.Backend == "dxgi" ? options.ScanHz : Math.Min(options.ScanHz, 60);
                if (active is not null)
                    cap = Math.Min(cap, fpsGuard?.ScanCap ?? cap);
                nextCapture = began + (long)(Math.Max(1000d / cap, workMs / 0.8) * Stopwatch.Frequency / 1000);
                if (!capture.TryRead(reader.Pixels, out long presented))
                {
                    if (!capture.HasTarget)
                    {
                        if (hadTarget)
                            reader.ResetTracking();
                        hadTarget = false;
                        lock (_gate)
                        {
                            _tracker?.LoseCapture();
                            _warmScore.Clear();
                        }
                        nextCapture = began + Stopwatch.Frequency / 10;
                    }
                    continue;
                }
                hadTarget = true;
                long captured = Stopwatch.GetTimestamp();
                if (presented < _start || presented > captured ||
                    Stopwatch.GetElapsedTime(presented, captured) > TimeSpan.FromMilliseconds(100))
                    continue;

                var timestamp = Elapsed(presented);
                if (active is null && !options.FirstKillOnly)
                {
                    int? score = await reader.ReadScoreOnlyAsync(timestamp).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_tracker is null && timestamp >= _leaseStarted)
                            _warmScore.Observe(timestamp, score);
                        else if (_tracker is not null && timestamp < _leaseStarted)
                        {
                            // This frame was captured before playback even if OCR finished after the hotkey.
                            _warmScore.Observe(timestamp, score);
                            SeedFromWarmScore();
                        }
                    }
                    continue;
                }
                await reader.ReadAsync(timestamp).ConfigureAwait(false);
                if (active is null)
                    continue;
                workMs += 0.1 * (Stopwatch.GetElapsedTime(began).TotalMilliseconds - workMs);
                if (fpsGuard is not null && captured >= nextFps)
                {
                    nextFps = captured + Stopwatch.Frequency;
                    fpsGuard.Observe(await fpsGuard.ReadAsync(capture).ConfigureAwait(false), options.ScanHz);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate)
                _failure = $"Rathuum kill-feed reader: {error.Message}";
        }
    }

    private TimeSpan Now => Elapsed(Stopwatch.GetTimestamp());

    private TimeSpan Elapsed(long timestamp) => Stopwatch.GetElapsedTime(_start, timestamp);

    private long Absolute(TimeSpan elapsed) =>
        _start + (long)(((Int128)elapsed.Ticks * Stopwatch.Frequency + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);

    private sealed class Lease(RathuumLiveMonitor owner) : IRathuumMonitor
    {
        public RathuumGateReading Read() => owner.Read();
        public bool TryConsume(long expectedLaunch) => owner.TryConsume(expectedLaunch);
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._tracker = null;
                owner._leaseStarted = owner.Now;
                owner._wake.Set();
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _worker.GetAwaiter().GetResult();
        _wake.Dispose();
        _stop.Dispose();
    }
}
