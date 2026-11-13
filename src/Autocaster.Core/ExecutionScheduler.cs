using System.Diagnostics;

namespace Autocaster.Core;

public interface IInputBackend
{
    bool IsPressed(InputEvent input);
    void Send(InputEvent input);
}

public interface IDeadlineClock
{
    long Frequency { get; }
    long Timestamp { get; }
    void WaitUntil(long deadline, CancellationToken cancellation);
}

public enum LarvaHudState { Unavailable, Idle, Countdown }

public interface ILarvaTarget
{
    LarvaHudState ReadLarvaHud();
}

public sealed class StopwatchClock : IDeadlineClock
{
    public long Frequency => Stopwatch.Frequency;
    public long Timestamp => Stopwatch.GetTimestamp();

    public void WaitUntil(long deadline, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            long remaining = deadline - Timestamp;
            if (remaining <= 0)
                return;
            int milliseconds = Math.Max(1, (int)Math.Min(int.MaxValue, remaining * 1000d / Frequency));
            cancellation.WaitHandle.WaitOne(milliseconds);
        }
    }
}

public sealed class InputOwnership(IInputBackend backend)
{
    private readonly List<InputEvent> _held = [];

    public void Dispatch(InputEvent input)
    {
        if (input.IsDown)
        {
            if (_held.Contains(input) || backend.IsPressed(input))
                throw new InvalidOperationException($"{input.Parameter} is already down. Release it before running.");
            backend.Send(input);
            _held.Add(input);
        }
        else if (input.IsUp)
        {
            var down = new InputEvent(input.Kind == InputKind.KeyUp ? InputKind.KeyDown : InputKind.MouseButtonDown, input.Value);
            if (!_held.Contains(down))
                throw new InvalidOperationException($"{input.Parameter} is not held by this run.");
            backend.Send(input);
            _held.Remove(down);
        }
        else
        {
            backend.Send(input);
        }
    }

    public string? ReleaseAll()
    {
        List<string> failures = [];
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            var input = _held[i];
            try
            {
                backend.Send(input.Release);
                _held.RemoveAt(i);
            }
            catch (Exception error)
            {
                failures.Add($"{input.Parameter}: {error.Message}");
            }
        }
        return failures.Count == 0 ? null : $"Could not release input: {string.Join(" | ", failures)}";
    }
}

public sealed record ScheduleOptions(LatePolicy Policy = LatePolicy.Dispatch, int ThresholdMs = 30)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Policy) || ThresholdMs is < 1 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(ThresholdMs), "The late event threshold must be between 0.001 and 60 seconds.");
    }
}

public enum SendResult { Sent, Skipped, Failed }
public readonly record struct EventSample(ScheduledInput Event, double ActualMs, double LateMs, SendResult Result, string? Error);
public readonly record struct TimingStats(int Sent, int Late, int Skipped, int Errors, double Min, double Mean, double Max)
{
    public TimingStats Add(EventSample sample)
    {
        int count = Sent + Skipped + Errors;
        return new(
            Sent + (sample.Result == SendResult.Sent ? 1 : 0),
            Late + (sample.LateMs > 0 ? 1 : 0),
            Skipped + (sample.Result == SendResult.Skipped ? 1 : 0),
            Errors + (sample.Result == SendResult.Failed ? 1 : 0),
            count == 0 ? sample.LateMs : Math.Min(Min, sample.LateMs),
            Mean + (sample.LateMs - Mean) / (count + 1),
            Math.Max(Max, sample.LateMs));
    }
}

public sealed class TargetLostException() : Exception("The target window lost focus or closed.");

internal sealed class TimedKeyReleases
{
    private readonly Dictionary<InputEvent, long> _deadlines = [];
    internal Dictionary<int, long> ProtectedKeyInputs { get; } = [];

    internal long? Next => _deadlines.Count == 0 ? null : _deadlines.Values.Min();

    internal bool Contains(InputEvent input) => _deadlines.ContainsKey(input);

    internal bool Hold(InputEvent input, long duration, IDeadlineClock clock, Action<InputEvent> send)
    {
        if (Contains(input))
            return false;
        send(input);
        _deadlines.Add(input, clock.Timestamp + duration);
        return true;
    }

    internal void ReleaseDue(long now, Action<InputEvent> send)
    {
        while (Next is long deadline && deadline <= now)
        {
            var input = _deadlines.First(item => item.Value == deadline).Key;
            send(input.Release);
            _deadlines.Remove(input);
        }
    }

    internal void Clear()
    {
        _deadlines.Clear();
        ProtectedKeyInputs.Clear();
    }
}

public sealed class ExecutionScheduler(IDeadlineClock clock)
{
    public long Timestamp => clock.Timestamp;
    public double MillisecondsSince(long start) => (clock.Timestamp - start) * 1000d / clock.Frequency;

    internal void WaitMilliseconds(int milliseconds, CancellationToken cancellation) =>
        clock.WaitUntil(clock.Timestamp + Math.Max(1, (long)milliseconds * clock.Frequency / 1000), cancellation);

    public void Execute(ExecutionPlan plan, long start, ScheduleOptions options, Func<bool> hasFocus,
        Action<InputEvent> send, Action<EventSample> observe, CancellationToken cancellation, Action? startMacroTimer = null) =>
        Execute(plan, start, options, hasFocus, send, observe, cancellation, startMacroTimer is null ? null : _ => startMacroTimer());

    public void Execute(ExecutionPlan plan, long start, ScheduleOptions options, Func<bool> hasFocus,
        Action<InputEvent> send, Action<EventSample> observe, CancellationToken cancellation, Action<int?>? startMacroTimer) =>
        Execute(plan, start, options, hasFocus, send, observe, cancellation, new(), drainReleases: true, startMacroTimer);

    private static void CheckRathuumReading(HudGate gate, RathuumGateReading reading)
    {
        if (reading.Failure is { } failure)
            throw new ScenarioException(failure, gate.ActionId);
        if (reading.State.Status is RathuumWaveStatus.MatchComplete or RathuumWaveStatus.Uncertain)
            throw new ScenarioException($"Rathuum kill feed: {reading.State.Reason}", gate.ActionId);
    }

    internal void Execute(ExecutionPlan plan, long start, ScheduleOptions options, Func<bool> hasFocus,
        Action<InputEvent> send, Action<EventSample> observe, CancellationToken cancellation,
        TimedKeyReleases releases, bool drainReleases, Action<int?>? startMacroTimer = null,
        ILarvaTarget? larva = null, Action<HudGate?, long>? timelineChanged = null,
        Func<RathuumGateOptions, IRathuumMonitor>? getRathuumMonitor = null, Action<string>? reportRathuum = null,
        Action<RathuumGateReading>? observeRathuum = null)
    {
        options.Validate();
        long frequency = clock.Frequency;
        long probe = Math.Max(1, frequency / 40); // Check the foreground during long waits every 25 ms.
        var suppressed = new HashSet<InputEvent>();
        var triggers = startMacroTimer is null ? [] : plan.TimerTriggers;
        var gates = plan.HudGates;
        if (larva is null && gates.Any(gate => gate.Rathuum is null))
            throw new ScenarioException("The current target cannot read the Larva countdown.");
        int nextTrigger = 0;
        int nextGate = 0;
        long timelineStart = start;

        long Ticks(int milliseconds) => (long)(((Int128)milliseconds * frequency + 999) / 1000);

        void Wait(long deadline)
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!hasFocus())
                    throw new TargetLostException();
                long now = clock.Timestamp;
                releases.ReleaseDue(now, send);
                now = clock.Timestamp;
                if (now >= deadline)
                    return;
                clock.WaitUntil(Math.Min(Math.Min(deadline, now + probe), releases.Next ?? long.MaxValue), cancellation);
            }
        }

        void WaitForLarva(HudGate gate)
        {
            long deadline = clock.Timestamp + Ticks(gate.TimeoutMs);
            bool wasIdle = false;
            int consecutive = 0;
            while (clock.Timestamp < deadline)
            {
                var state = larva!.ReadLarvaHud();
                wasIdle |= state == LarvaHudState.Idle;
                consecutive = wasIdle && state == LarvaHudState.Countdown ? consecutive + 1 : 0;
                if (consecutive == 2 && clock.Timestamp < deadline)
                {
                    return;
                }
                Wait(Math.Min(deadline, clock.Timestamp + Ticks(8)));
            }
            throw new ScenarioException("Larva countdown did not appear before the timeout.", gate.ActionId);
        }

        void WaitForRathuum(HudGate gate, RathuumGateOptions options)
        {
            var monitor = getRathuumMonitor?.Invoke(options) ??
                throw new ScenarioException("The current target cannot read the Rathuum kill feed.", gate.ActionId);
            long deadline = clock.Timestamp + Ticks(gate.TimeoutMs);
            RathuumGateReading reading = default;
            while (clock.Timestamp < deadline)
            {
                Wait(clock.Timestamp);
                reading = monitor.Read();
                observeRathuum?.Invoke(reading);
                CheckRathuumReading(gate, reading);
                if (reading.LaunchAt is long launch && launch <= clock.Timestamp &&
                    clock.Timestamp < deadline && monitor.TryConsume(launch))
                {
                    reportRathuum?.Invoke(options.FirstKillOnly
                        ? $"Rathuum kill feed: first-kill launch with {options.FirstKillLeadMs} ms lead."
                        : $"Rathuum kill feed: released with {options.ReadCorrectionMs} ms reading correction.");
                    return;
                }
                long nextProbe = clock.Timestamp + Ticks(12);
                if (reading.LaunchAt is long pending && pending > clock.Timestamp)
                    nextProbe = Math.Min(nextProbe, pending);
                Wait(Math.Min(deadline, nextProbe));
            }
            string detail = reading.State.Reason ?? $"{reading.State.NamedKills} named kills confirmed.";
            throw new ScenarioException($"Rathuum kill feed timed out. {detail}", gate.ActionId);
        }

        void WaitForHud(HudGate gate)
        {
            Wait(timelineStart + Ticks(gate.OffsetMs));
            long began = clock.Timestamp;
            timelineChanged?.Invoke(gate, timelineStart);
            if (gate.Rathuum is { } rathuum)
                WaitForRathuum(gate, rathuum);
            else
                WaitForLarva(gate);
            timelineStart += clock.Timestamp - began;
            timelineChanged?.Invoke(null, timelineStart);
        }

        void SignalsThrough(int dispatched)
        {
            while (true)
            {
                bool timerDue = nextTrigger < triggers.Length && triggers[nextTrigger].EventsThrough <= dispatched;
                bool gateDue = nextGate < gates.Length && gates[nextGate].EventsThrough <= dispatched;
                if (!timerDue && !gateDue)
                    return;
                if (gateDue && (!timerDue || gates[nextGate].Order < triggers[nextTrigger].Order))
                    WaitForHud(gates[nextGate++]);
                else
                {
                    var trigger = triggers[nextTrigger++];
                    Wait(timelineStart + Ticks(trigger.OffsetMs));
                    startMacroTimer!(trigger.DurationMs);
                }
            }
        }

        foreach (var item in plan.Events)
        {
            SignalsThrough(item.Number - 1);
            Wait(timelineStart + Ticks(item.OffsetMs));
            if (item.MinimumKeyIntervalMs is int minimum &&
                releases.ProtectedKeyInputs.TryGetValue(item.Input.Value, out long previous))
                Wait(previous + Ticks(minimum));
            double actual = MillisecondsSince(timelineStart);
            double late = actual - item.OffsetMs;
            if (late > options.ThresholdMs && options.Policy == LatePolicy.Stop)
            {
                var error = $"Event {item.Number} is {late / 1000:F4} s late.";
                observe(new(item, actual, late, SendResult.Skipped, error));
                throw new InvalidOperationException(error);
            }
            if (late > options.ThresholdMs && options.Policy == LatePolicy.SkipWheel && item.Type == InputKind.MouseWheel)
            {
                observe(new(item, actual, late, SendResult.Skipped, null));
                continue;
            }

            var result = SendResult.Sent;
            try
            {
                if (item.TimedHoldMs is int hold)
                {
                    if (!releases.Hold(item.Input, Ticks(hold), clock, send))
                        result = SendResult.Skipped;
                }
                else if (item.Type == InputKind.KeyDown && releases.Contains(item.Input))
                {
                    suppressed.Add(item.Input.Release);
                    result = SendResult.Skipped;
                }
                else if (suppressed.Remove(item.Input))
                    result = SendResult.Skipped;
                else
                    send(item.Input);
                if (result == SendResult.Sent && item.MinimumKeyIntervalMs.HasValue)
                    releases.ProtectedKeyInputs[item.Input.Value] = clock.Timestamp;
            }
            catch (Exception error) when (error is not OperationCanceledException and not TargetLostException)
            {
                observe(new(item, actual, late, SendResult.Failed, error.Message));
                throw;
            }
            observe(new(item, actual, late, result, null));
        }
        SignalsThrough(plan.Events.Length);
        Wait(timelineStart + Ticks(plan.DurationMs));
        while (drainReleases && releases.Next is long release)
            Wait(release);
    }
}
