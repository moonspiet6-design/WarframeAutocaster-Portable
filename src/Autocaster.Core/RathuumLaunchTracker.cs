namespace Autocaster.Core;

public sealed class RathuumLaunchTracker(RathuumTimingOptions options, TimeSpan? trackingStarted = null)
{
    private static readonly TimeSpan FreshFrame = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan RecentScore = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan RecentFirstKillFrame = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan DispatchGrace = TimeSpan.FromMilliseconds(25);
    private readonly RathuumWaveTimer _timer = new(options, trackingStarted);
    private RathuumWaveForecast? _armed;
    private TimeSpan? _lastUsableFrame;
    private TimeSpan? _lastFrame;
    private TimeSpan? _lastMatchingScore;
    private string? _missedLaunch;
    private int _consumedWave;

    public RathuumWaveState State
    {
        get
        {
            var state = _timer.State;
            return state.Wave > 0 && state.Wave <= _consumedWave &&
                state.Status is RathuumWaveStatus.Ready or RathuumWaveStatus.Expired
                ? state with { Status = RathuumWaveStatus.Gathering, NamedKills = 0, Reason = "Waiting for the next wave." }
                : state;
        }
    }

    public void BeginWave(TimeSpan now, int score) => _timer.BeginWave(now, score);

    public void SetWaveSize(int size)
    {
        if (_timer.SetWaveSize(size, nextWaveOnly: _timer.State.Wave > 0 && _timer.State.Wave <= _consumedWave) &&
            _armed is { } pending && pending.Wave == _timer.State.Wave)
            _armed = null;
    }

    public void Observe(TimeSpan timestamp, TimeSpan processingTime, IReadOnlyList<RathuumFeedEvent> events,
        int? score, IReadOnlyList<string> visible, bool feedUsable)
    {
        if (_lastFrame is { } previous && timestamp <= previous)
            return;
        _lastFrame = timestamp;
        _timer.Observe(timestamp, events, score, visible, feedUsable);
        bool matchingScore = score.HasValue && score == _timer.State.Score;
        if (matchingScore)
            _lastMatchingScore = timestamp;
        else if (score.HasValue)
            _lastMatchingScore = null;
        bool current = feedUsable && matchingScore;
        if (current)
            _lastUsableFrame = timestamp;
        else if (score > _timer.State.Score)
            _lastUsableFrame = null;

        if (MissedLaunch(processingTime) is not null)
            return;

        // A signal-based launch can still be pending when the timer starts observing the next wave.
        if (_timer.State.Status != RathuumWaveStatus.MatchComplete && _armed is { FirstKillConfirmedAt: not null } pending &&
            pending.Wave < _timer.State.Wave && NextLaunch(processingTime) is not null)
            return;

        // Evidence must arrive before the moment at which it asks the macro to act.
        if (_timer.EligibleForecast is not { } next)
            _armed = null;
        else if ((options.FirstKillOnly || (next.FirstKillConfirmedAt.HasValue ? matchingScore : current)) &&
            processingTime < next.SuggestedLaunch)
            _armed = next;
        else if (_armed is { } armed && (armed.Wave != next.Wave || armed.LaunchWindow != next.LaunchWindow))
            _armed = null;
    }

    public void LoseCapture()
    {
        _lastUsableFrame = _lastFrame = _lastMatchingScore = null;
        _armed = null;
    }

    public TimeSpan? NextLaunch(TimeSpan now) =>
        _missedLaunch is null && _armed is { } forecast && forecast.Wave > _consumedWave &&
        (forecast.FirstKillConfirmedAt.HasValue
            ? now <= forecast.SuggestedLaunch + DispatchGrace
            : now <= forecast.LaunchWindow.Latest && now < forecast.FirstRespawn.Earliest)
            ? forecast.SuggestedLaunch : null;

    public bool TryConsume(TimeSpan now)
    {
        if (NextLaunch(now) is not { } launch || now < launch || _armed is not { } forecast ||
            !HasFreshEvidence(now, forecast.FirstKillConfirmedAt.HasValue))
            return false;
        _consumedWave = forecast.Wave;
        return true;
    }

    public string? MissedLaunch(TimeSpan now)
    {
        if (_missedLaunch is null && _armed is { FirstKillConfirmedAt: not null } forecast &&
            forecast.Wave > _consumedWave && now > forecast.SuggestedLaunch + DispatchGrace)
        {
            string detail = HasFreshEvidence(now, signalTiming: true)
                ? "The dispatch deadline passed."
                : options.FirstKillOnly ? "A recent game frame was unavailable."
                : "A recent frame and matching team score were unavailable.";
            _missedLaunch = $"Rathuum wave {forecast.Wave}: missed the launch window. {detail}";
        }
        return _missedLaunch;
    }

    private bool HasFreshEvidence(TimeSpan now, bool signalTiming) => options.FirstKillOnly
        ? _lastFrame is { } firstKillFrame && now - firstKillFrame <= RecentFirstKillFrame
        : signalTiming
            ? _lastFrame is { } frame && now - frame <= FreshFrame &&
                _lastMatchingScore is { } score && now - score <= RecentScore
            : _lastUsableFrame is { } usable && now - usable <= FreshFrame;
}
