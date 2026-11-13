namespace Autocaster.Core;

public sealed record RathuumTimingOptions(int WaveSize = 5)
{
    public TimeSpan Respawn { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan RespawnUncertainty { get; init; } = TimeSpan.FromMilliseconds(25);
    public TimeSpan Lead { get; init; } = TimeSpan.FromMilliseconds(150);
    public TimeSpan? ReadCorrection { get; init; }
    public bool FirstKillOnly { get; init; }
    public RathuumTimeRange FeedDelay { get; init; } = new(
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(550));
    public TimeSpan MaximumLateFeedDelay { get; init; } = TimeSpan.FromSeconds(2);
    // OCR can lose the fading text before the last visible pixel.
    public RathuumTimeRange DisappearanceDelay { get; init; } = new(
        TimeSpan.FromMilliseconds(4800), TimeSpan.FromMilliseconds(5400));
    public TimeSpan MaximumLaunchUncertainty { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan ScoreDisplayDelay { get; init; } = TimeSpan.FromMilliseconds(25);
    public bool StartAtCurrentScore { get; init; }
}

public readonly record struct RathuumTimeRange(TimeSpan Earliest, TimeSpan Latest)
{
    public TimeSpan Width => Latest - Earliest;
    public TimeSpan Midpoint => Earliest + Width / 2;

    internal bool Overlaps(RathuumTimeRange other) =>
        Earliest <= other.Latest && other.Earliest <= Latest;

    internal RathuumTimeRange Shift(TimeSpan offset) => new(Earliest + offset, Latest + offset);
}

public enum RathuumWaveStatus { AwaitingBaseline, Gathering, Tentative, Ready, Expired, Uncertain, MatchComplete }

public readonly record struct RathuumWaveState(int Wave, int? StartingScore, int? Score,
    int NamedKills, RathuumWaveStatus Status, string? Reason);

public readonly record struct RathuumWaveForecast(int Wave, RathuumTimeRange FirstRespawn,
    RathuumTimeRange LastRespawn, RathuumTimeRange LaunchWindow, int ScoreMatchedKills,
    int ConfirmedDisappearances)
{
    public TimeSpan SuggestedLaunch => LaunchWindow.Midpoint;
    public bool UsedDisappearanceTiming { get; init; }
    public bool UsedLateFeedMatching { get; init; }
    public bool UsedFeedTimingForFirstKill { get; init; }
    public bool UsedFirstKillWithoutScoreChange { get; init; }
    public TimeSpan? FirstKillConfirmedAt { get; init; }
}

public sealed class RathuumWaveTimer
{
    private static readonly TimeSpan MaxFrameGap = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan ScoreConfirmationGap = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan DisappearanceConfirmation = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan FirstKillFeedAllowance = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FirstKillScoreWait = TimeSpan.FromMilliseconds(850);
    private static readonly TimeSpan LateIdentityVisibility = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LateIdentityDisappearance = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan CurrentScoreConfirmation = TimeSpan.FromMilliseconds(32);
    private static readonly TimeSpan MatchResetConfirmation = TimeSpan.FromMilliseconds(500);
    private readonly RathuumTimingOptions _options;
    private int _waveSize;
    private int _requestedWaveSize;
    private readonly List<Kill> _kills = [];
    private readonly List<RathuumTimeRange> _scoreSlots = [];
    private readonly List<RathuumTimeRange> _summons = [];
    private TimeSpan? _lastFrame;
    private TimeSpan? _trackingStarted;
    private TimeSpan _waveStarted;
    private int _wave;
    private int? _baseline;
    private int? _score;
    private TimeSpan? _scoreLastSeen;
    private TimeSpan? _scoreVisibleSince;
    private int? _candidateScore;
    private int _candidateReads;
    private TimeSpan _candidateFirstSeen;
    private TimeSpan _candidateLastSeen;
    private string? _conflict;
    private string? _tentativeReason;
    private bool _waveReconciled;
    private RathuumWaveForecast? _forecast;
    private bool _dirty;

    public RathuumWaveTimer(RathuumTimingOptions options, TimeSpan? trackingStarted = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.WaveSize is not (5 or 8) || options.Respawn <= TimeSpan.Zero ||
            options.RespawnUncertainty < TimeSpan.Zero || options.RespawnUncertainty >= options.Respawn ||
            (options.ReadCorrection is { } correction
                ? correction < TimeSpan.Zero || correction >= options.Respawn
                : options.Lead < TimeSpan.Zero || options.Lead >= options.Respawn - options.RespawnUncertainty) ||
            options.FeedDelay.Earliest < TimeSpan.Zero || options.FeedDelay.Width < TimeSpan.Zero ||
            options.FeedDelay.Latest > options.MaximumLateFeedDelay || options.FeedDelay.Latest >= options.Respawn ||
            options.DisappearanceDelay.Earliest < TimeSpan.Zero || options.DisappearanceDelay.Width < TimeSpan.Zero ||
            options.MaximumLaunchUncertainty < TimeSpan.Zero || options.ScoreDisplayDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid wave timing calibration.");
        _options = options;
        _trackingStarted = trackingStarted;
        _waveSize = _requestedWaveSize = options.WaveSize;
    }

    public RathuumWaveState State { get; private set; } =
        new(0, null, null, 0, RathuumWaveStatus.AwaitingBaseline, "Waiting for a stable zero score or a known wave start.");

    public RathuumWaveForecast? EligibleForecast => _tentativeReason is null ? _forecast : null;

    internal bool SetWaveSize(int size, bool nextWaveOnly)
    {
        if (size is not (5 or 8))
            throw new ArgumentOutOfRangeException(nameof(size));
        if (_options.FirstKillOnly)
            return false;
        _requestedWaveSize = size;
        if (nextWaveOnly || size == _waveSize || _kills.Count > size || _scoreSlots.Count > size)
            return false;

        _waveSize = size;
        _forecast = null;
        _waveReconciled = false;
        _tentativeReason = null;
        _dirty = true;
        return true;
    }

    // Call only at a known wave boundary. Automatic tracking starts from an observed zero score.
    public void BeginWave(TimeSpan now, int scoreBefore)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(now.Ticks);
        if (scoreBefore is < 0 or > 25 || (_lastFrame is TimeSpan last && now < last))
            throw new ArgumentOutOfRangeException(nameof(scoreBefore), "The baseline score and time must be valid.");

        _trackingStarted ??= now;
        _score = scoreBefore;
        _scoreLastSeen = now;
        _scoreVisibleSince = now;
        _candidateScore = null;
        StartWave(now, scoreBefore);
        State = new(_wave, _baseline, _score, 0, RathuumWaveStatus.Gathering, null);
    }

    public RathuumWaveForecast? Observe(TimeSpan now, IReadOnlyList<RathuumFeedEvent> feedEvents,
        int? rawScore, IReadOnlyList<string> visibleExecutioners, bool feedUsable)
    {
        if (now < TimeSpan.Zero || (_lastFrame is TimeSpan previous && now <= previous))
            return _forecast;

        bool continuous = _lastFrame is TimeSpan last && now - last <= MaxFrameGap;
        _lastFrame = now;
        _trackingStarted ??= now;
        if (continuous && _conflict is null && _waveReconciled && _forecast is { } complete &&
            now > complete.LastRespawn.Latest && !(_candidateScore > _score))
            StartWave(complete.LastRespawn.Latest, _score);

        ObserveScore(now, rawScore, continuous);
        if (_score == 25)
        {
            _forecast = null;
            State = new(_wave, _baseline, _score, _kills.Count, RathuumWaveStatus.MatchComplete, "The match reached 25 points.");
            return null;
        }
        bool allowFeedOnly = _options.FirstKillOnly && _options.StartAtCurrentScore;
        if (_baseline is null && !allowFeedOnly)
        {
            State = new(0, null, _score, 0, RathuumWaveStatus.AwaitingBaseline,
                _options.StartAtCurrentScore ? "Waiting for a stable team score." : "Waiting for a stable zero score.");
            return null;
        }

        ObserveFeed(feedEvents, visibleExecutioners, feedUsable);
        ObserveVisibility(now, visibleExecutioners, feedUsable && continuous);
        if (_dirty)
        {
            if (_options.FirstKillOnly)
                RecalculateFirstKill();
            else
                Recalculate();
        }

        if (allowFeedOnly && _forecast is null &&
            _scoreSlots.Count == 0 && _kills.Count > 0)
            TryEstimateFirstKillWithoutScoreChange(now);

        if (!_options.FirstKillOnly && _conflict is null && _kills.Count < _waveSize && _kills.Count > 0 &&
            now >= _kills.Min(kill => kill.Estimate.Earliest) + _options.Respawn - _options.RespawnUncertainty)
            Invalidate("An earlier death could already have respawned before the full wave was accounted for.");

        if (_conflict is not null)
            State = new(_wave, _baseline, _score, _kills.Count, RathuumWaveStatus.Uncertain, _conflict);
        else if (_forecast is not { } forecast)
            State = new(_wave, _baseline, _score, _kills.Count, RathuumWaveStatus.Gathering, _tentativeReason);
        else if (_tentativeReason is not null)
            State = new(_wave, _baseline, _score, _kills.Count, RathuumWaveStatus.Tentative, _tentativeReason);
        else if (now >= forecast.SuggestedLaunch)
            State = new(_wave, _baseline, _score, _kills.Count, RathuumWaveStatus.Expired, "The suggested launch time has passed.");
        else
            State = new(_wave, _baseline, _score, _kills.Count, RathuumWaveStatus.Ready,
                forecast.UsedFirstKillWithoutScoreChange
                    ? (_score is null ? "Using a new executioner entry while the team score is unreadable." :
                        "Using a new executioner entry while the team score is unchanged.") :
                forecast.UsedFeedTimingForFirstKill ? "First-kill timing refined using the feed because the score interval was wide." :
                forecast.UsedLateFeedMatching ? "Late names reconciled against the confirmed score." : null);
        return _forecast;
    }

    private void StartWave(TimeSpan now, int? baseline)
    {
        bool preserveEarlyFeed = _wave == 0 && _options.FirstKillOnly && _options.StartAtCurrentScore;
        _wave++;
        _waveSize = _requestedWaveSize;
        _waveStarted = now;
        _baseline = baseline;
        _scoreSlots.Clear();
        if (!preserveEarlyFeed)
        {
            _kills.Clear();
            _summons.Clear();
        }
        _forecast = null;
        _conflict = null;
        _waveReconciled = false;
        _tentativeReason = null;
        _dirty = false;
    }

    private void ObserveScore(TimeSpan now, int? raw, bool continuous)
    {
        if (!continuous || now - _candidateLastSeen > ScoreConfirmationGap)
            _candidateScore = null;
        if (raw is null or < 0 or > 25)
            return;

        if (raw == _score)
        {
            NoteVisibleScore(now);
            _candidateScore = null;
            return;
        }

        if (raw < _score && !(raw == 0 && _score == 25))
        {
            _candidateScore = null;
            return;
        }

        if (raw != _candidateScore)
        {
            _candidateScore = raw;
            _candidateReads = 1;
            _candidateFirstSeen = now;
            _candidateLastSeen = now;
            return;
        }

        _candidateLastSeen = now;
        _candidateReads++;
        bool seedCurrent = _options.StartAtCurrentScore && _baseline is null && raw is > 0 and < 25;
        if (raw == 0 && _score == 25 && now - _candidateFirstSeen < MatchResetConfirmation)
            return;
        if (seedCurrent && (_candidateReads < 3 || now - _candidateFirstSeen < CurrentScoreConfirmation))
            return;

        int next = raw.Value;
        int? old = _score;
        _score = next;
        TimeSpan firstSeen = _candidateFirstSeen;
        TimeSpan previous = _scoreLastSeen ?? _waveStarted;
        NoteVisibleScore(now);
        _candidateScore = null;

        if (_wave > 0 && _baseline is null && old is null && next < 25)
        {
            // Attach the newly readable score without discarding a deadline already set by the feed.
            _baseline = next;
            return;
        }

        if (next == 0 || seedCurrent)
        {
            StartWave(now, next);
            return;
        }
        if (_baseline is null || old is null || _conflict is not null)
            return;
        if (!_options.FirstKillOnly && next - _baseline.Value > _waveSize)
        {
            Invalidate($"Expected {_waveSize} kills, but the score advanced by {next - _baseline.Value} before the next wave. Check the Rathuum 5 / 8 setting.");
            return;
        }

        var bracket = new RathuumTimeRange(Max(_waveStarted, previous - _options.ScoreDisplayDelay), firstSeen);
        for (int i = old.Value; i < next; i++)
            _scoreSlots.Add(bracket);
        _dirty = true;
    }

    private void NoteVisibleScore(TimeSpan now)
    {
        if (_scoreLastSeen is not TimeSpan last || now - last > MaxFrameGap)
            _scoreVisibleSince = now;
        _scoreLastSeen = now;
    }

    private void ObserveFeed(IReadOnlyList<RathuumFeedEvent> events, IReadOnlyList<string> visible, bool reliable)
    {
        if (_conflict is not null)
            return;

        var feedStart = _wave <= 1 && _options.FirstKillOnly && _options.StartAtCurrentScore
            ? _trackingStarted ?? _waveStarted : _waveStarted;
        foreach (var hit in events)
        {
            if (hit.FirstSeen < feedStart || (_options.FirstKillOnly && hit.FirstSeen - _options.FeedDelay.Earliest < feedStart))
                continue;
            var estimate = new RathuumTimeRange(Max(feedStart, hit.FirstSeen - _options.FeedDelay.Latest),
                Max(feedStart, hit.FirstSeen - _options.FeedDelay.Earliest));
            if (hit.Kind == RathuumFeedEventKind.Summon)
            {
                _summons.Add(estimate);
                _dirty = true;
                continue;
            }

            var existing = _kills.Find(kill => kill.Name == hit.Name);
            if (hit.Kind == RathuumFeedEventKind.RepeatedName)
            {
                if (!_options.FirstKillOnly && existing is not null && reliable && visible.Count(name => name == hit.Name) > 1)
                    Invalidate("Two simultaneous feed rows contain the same executioner name.");
                continue;
            }
            if (_options.FirstKillOnly && existing is not null)
                continue;
            if (!_options.FirstKillOnly && (existing is not null || _kills.Count >= _waveSize))
            {
                Invalidate("The kill feed crossed the wave boundary without a reconciled respawn.");
                return;
            }
            _kills.Add(new(hit.Name, estimate, Max(feedStart, hit.FirstSeen - _options.MaximumLateFeedDelay), hit.Confirmed));
            _dirty = true;
        }
    }

    private void ObserveVisibility(TimeSpan now, IReadOnlyList<string> visible, bool reliable)
    {
        foreach (var kill in _kills)
        {
            if (!reliable)
            {
                kill.LastVisible = null;
                kill.FirstMissing = null;
                kill.VisibleSince = null;
                continue;
            }

            if (visible.Contains(kill.Name))
            {
                if (kill.LastVisible is not TimeSpan lastSeen || now - lastSeen > MaxFrameGap)
                    kill.VisibleSince = now;
                kill.LastVisible = now;
                kill.FirstMissing = null;
                if (kill.Disappearance is not null)
                {
                    kill.Disappearance = null;
                    kill.LateIdentityQualified = false;
                    _dirty = true;
                }
                continue;
            }

            if (kill.LastVisible is not TimeSpan last || kill.LateIdentityQualified)
                continue;
            kill.FirstMissing ??= now;
            TimeSpan missing = now - kill.FirstMissing.Value;
            if (missing < DisappearanceConfirmation)
                continue;

            // A missing row is evidence only near its calibrated end, following uninterrupted observations.
            var fade = new RathuumTimeRange(Max(kill.EarliestLateDeath, last - _options.DisappearanceDelay.Latest),
                Min(kill.Estimate.Latest, kill.FirstMissing.Value - _options.DisappearanceDelay.Earliest));
            if (fade.Width < TimeSpan.Zero)
            {
                kill.LastVisible = null;
                kill.FirstMissing = null;
                continue;
            }

            if (kill.Disappearance is null && fade.Overlaps(kill.Estimate))
            {
                kill.Disappearance = fade;
                _dirty = true;
            }

            // A readable score through the missing interval also rules out a completely hidden HUD.
            if (kill.VisibleSince is TimeSpan since && last - since >= LateIdentityVisibility &&
                missing >= LateIdentityDisappearance && _scoreVisibleSince <= kill.FirstMissing &&
                _scoreLastSeen >= now - MaxFrameGap)
            {
                kill.Disappearance = fade;
                kill.LateIdentityQualified = true;
                _dirty = true;
            }
        }
    }

    private void RecalculateFirstKill()
    {
        _dirty = false;
        // Later rows can overlap a grouped score change. Keep the first confirmed deadline.
        if (_forecast.HasValue && _tentativeReason is null)
            return;

        _waveReconciled = false;
        _forecast = null;
        _tentativeReason = null;
        if (_conflict is not null || _scoreSlots.Count == 0 || _kills.Count == 0)
            return;

        foreach (var group in _scoreSlots.GroupBy(slot => slot))
        {
            var slot = group.Key;
            var matches = _kills.Select(kill => (Kill: kill, Window: kill.Estimate.Overlaps(slot)
                    ? kill.Estimate : kill.LateIdentityQualified ? kill.Disappearance : null))
                .Where(match => match.Window is { } window && window.Overlaps(slot)).ToArray();
            // A grouped score increase can account for both summons and executioners.
            int possibleSummons = _summons.Count(slot.Overlaps);
            if (possibleSummons >= group.Count())
            {
                if (matches.Length == 0)
                    continue;
                _tentativeReason = "Summons could account for all points in the first score change.";
                return;
            }
            bool usedLateMatch = false;
            if (matches.Length == 0 && slot.Width <= _options.MaximumLaunchUncertainty &&
                !_summons.Any(summon => new RathuumTimeRange(summon.Earliest - FirstKillFeedAllowance, summon.Latest).Overlaps(slot)))
            {
                matches = _kills.Where(kill => !_scoreSlots.Any(later => later.Earliest > slot.Latest && kill.Estimate.Overlaps(later)))
                    .Select(kill => (Kill: kill, Window: (RathuumTimeRange?)new RathuumTimeRange(
                        Max(kill.EarliestLateDeath, kill.Estimate.Earliest - FirstKillFeedAllowance), kill.Estimate.Latest)))
                    .Where(match => match.Window!.Value.Overlaps(slot)).ToArray();
                usedLateMatch = matches.Length > 0;
            }
            if (matches.Length == 0)
            {
                _tentativeReason = "Waiting for a named executioner to confirm the first score change.";
                return;
            }

            var windows = matches.Select(match => match.Window!.Value).ToArray();
            var death = new RathuumTimeRange(windows.Min(window => Max(slot.Earliest, window.Earliest)),
                windows.Max(window => Min(slot.Latest, window.Latest)));
            var respawn = ToRespawn(death);
            bool useFeedTiming = death.Width > _options.MaximumLaunchUncertainty;
            TimeSpan deathTime = useFeedTiming
                ? Max(death.Earliest, Min(death.Latest, matches.Min(match => match.Kill.Estimate.Midpoint)))
                : death.Midpoint;
            TimeSpan launch = deathTime + _options.Respawn - _options.Lead;
            _forecast = new(_wave, respawn, respawn, new(launch, launch), 1, 0)
            {
                FirstKillConfirmedAt = matches.Min(match => match.Kill.Confirmed),
                UsedLateFeedMatching = usedLateMatch,
                UsedFeedTimingForFirstKill = useFeedTiming
            };
            if (launch >= respawn.Earliest)
                _tentativeReason = "The first score change is too imprecise for the configured lead.";
            _waveReconciled = _tentativeReason is null;
            return;
        }
    }

    private void TryEstimateFirstKillWithoutScoreChange(TimeSpan now)
    {
        var first = _kills.MinBy(kill => kill.Estimate.Latest)!;
        if (now - first.Confirmed < FirstKillScoreWait || _summons.Any(first.Estimate.Overlaps))
            return;

        var respawn = ToRespawn(first.Estimate);
        var launch = first.Estimate.Midpoint + _options.Respawn - _options.Lead;
        if (launch <= now || launch >= respawn.Earliest)
            return;

        if (_wave == 0)
        {
            _wave = 1;
            _waveStarted = _trackingStarted ?? now;
        }
        _forecast = new(_wave, respawn, respawn, new(launch, launch), 0, 0)
        {
            FirstKillConfirmedAt = first.Confirmed,
            UsedFirstKillWithoutScoreChange = true
        };
        _waveReconciled = true;
    }

    private void Recalculate()
    {
        _dirty = false;
        _waveReconciled = false;
        if (_conflict is not null || _kills.Count != _waveSize)
            return;

        var windows = _kills.Select(kill => kill.Estimate).ToArray();
        RathuumTimeRange[] deathWindows = windows;
        int scoreMatched = 0, firstSlotKills = 0;
        int disappearanceChecks = 0;
        bool usedDisappearanceTiming = false, usedLateFeedMatching = false;
        string? reason = null;
        if (_scoreSlots.Count != windows.Length)
            reason = "The expected number of score changes is not yet established.";
        else if (_scoreSlots.Any(slot => _summons.Any(slot.Overlaps)))
            reason = "A summon can account for one of these score changes.";
        else
        {
            var matched = MatchScore(windows, _scoreSlots, null, 0, out var slotWindows, out firstSlotKills);
            if (matched is null && _kills.Any(kill => kill.LateIdentityQualified))
            {
                var lateWindows = _kills.Select(kill => kill.LateIdentityQualified ? kill.Disappearance : null).ToArray();
                matched = MatchScore(windows, _scoreSlots, lateWindows, windows.Length, out slotWindows, out firstSlotKills);
                usedDisappearanceTiming = matched is not null;
            }
            if (matched is null && _options.ReadCorrection.HasValue && !_scoreSlots.Any(slot => _summons.Any(summon =>
                slot.Overlaps(new(Max(_waveStarted, summon.Earliest - (_options.MaximumLateFeedDelay - _options.FeedDelay.Latest)),
                    summon.Latest)))))
            {
                // The complete score still has to match every distinct name. Allow a minority of OCR rows
                // to be late if a tightly observed score change can account for each of them.
                var lateWindows = _kills.Select(kill => (RathuumTimeRange?)new RathuumTimeRange(
                    kill.EarliestLateDeath, kill.Estimate.Latest)).ToArray();
                matched = MatchScore(windows, _scoreSlots, lateWindows, (windows.Length - 1) / 2,
                    out slotWindows, out firstSlotKills);
                usedLateFeedMatching = matched is not null;
            }
            if (matched is null)
                reason = "The score changes do not fit all of the named deaths.";
            else
            {
                scoreMatched = windows.Length;
                windows = matched;
                deathWindows = slotWindows;
            }
        }

        for (int i = 0; i < windows.Length; i++)
        {
            if (_kills[i].Disappearance is not RathuumTimeRange disappearance)
                continue;
            if (windows[i].Overlaps(disappearance))
                disappearanceChecks++;
            else
                reason ??= "An observed row disappearance disagrees with the estimated death time.";
        }

        var first = new RathuumTimeRange(deathWindows.Min(window => window.Earliest),
            deathWindows.Min(window => window.Latest));
        var last = new RathuumTimeRange(deathWindows.Max(window => window.Earliest),
            deathWindows.Max(window => window.Latest));
        first = ToRespawn(first);
        if (last.Latest >= first.Earliest)
        {
            Invalidate("An earlier enemy could already have respawned before the last named death.");
            return;
        }
        last = ToRespawn(last);
        var launch = first.Shift(-_options.Lead);
        TimeSpan? firstConfirmed = null;
        if (_options.ReadCorrection is { } correction)
        {
            // The score establishes which names can belong to the first kill. The trigger uses its feed confirmation time.
            firstConfirmed = _kills.Where((_, index) => firstSlotKills == 0 || (firstSlotKills & (1 << index)) != 0)
                .Min(kill => kill.Confirmed);
            TimeSpan at = firstConfirmed.Value + _options.Respawn - correction;
            launch = new(at, at);
        }
        _forecast = new(_wave, first, last, launch, scoreMatched, disappearanceChecks)
        {
            UsedDisappearanceTiming = usedDisappearanceTiming,
            UsedLateFeedMatching = usedLateFeedMatching,
            FirstKillConfirmedAt = firstConfirmed
        };
        _waveReconciled = reason is null;
        if (launch.Width > _options.MaximumLaunchUncertainty)
            reason ??= "The first respawn interval is wider than the configured timing tolerance.";
        if (!firstConfirmed.HasValue && launch.Midpoint >= first.Earliest)
            reason ??= "The first respawn can occur before the proposed launch time. Increase lead or tighten calibration.";
        _tentativeReason = reason;
    }

    private RathuumTimeRange ToRespawn(RathuumTimeRange death) =>
        new(death.Earliest + _options.Respawn - _options.RespawnUncertainty,
            death.Latest + _options.Respawn + _options.RespawnUncertainty);

    // Each score increment is one slot. Keep only edges that can participate in a complete assignment.
    private static RathuumTimeRange[]? MatchScore(RathuumTimeRange[] kills, List<RathuumTimeRange> slots,
        RathuumTimeRange?[]? lateWindows, int lateBudget, out RathuumTimeRange[] slotWindows, out int firstSlotKills)
    {
        int count = kills.Length;
        slotWindows = [];
        firstSlotKills = 0;
        var candidates = new int[count];
        var exact = new int[count];
        var overlaps = new RathuumTimeRange[count, count];
        for (int kill = 0; kill < count; kill++)
        {
            for (int slot = 0; slot < count; slot++)
            {
                RathuumTimeRange window = kills[kill];
                if (!window.Overlaps(slots[slot]))
                {
                    if (lateWindows?[kill] is not RathuumTimeRange late || !late.Overlaps(slots[slot]) ||
                        slots[slot].Width > MaxFrameGap)
                        continue;
                    window = late;
                }
                else
                    exact[kill] |= 1 << slot;
                candidates[kill] |= 1 << slot;
                overlaps[kill, slot] = new(Max(window.Earliest, slots[slot].Earliest), Min(window.Latest, slots[slot].Latest));
            }
        }

        if (!CanAssign(candidates, exact, 0, 0, lateBudget))
            return null;

        var result = new RathuumTimeRange[count];
        slotWindows = new RathuumTimeRange[count];
        Array.Fill(slotWindows, new RathuumTimeRange(TimeSpan.MaxValue, TimeSpan.MinValue));
        for (int kill = 0; kill < count; kill++)
        {
            var earliest = TimeSpan.MaxValue;
            var latest = TimeSpan.MinValue;
            for (int slot = 0; slot < count; slot++)
            {
                int bit = 1 << slot;
                int remainingBudget = lateBudget - ((exact[kill] & bit) == 0 ? 1 : 0);
                if ((candidates[kill] & bit) == 0 || !CanAssign(candidates, exact, 0, bit, remainingBudget, kill))
                    continue;
                if (slot == 0)
                    firstSlotKills |= 1 << kill;
                var overlapStart = overlaps[kill, slot].Earliest;
                var overlapEnd = overlaps[kill, slot].Latest;
                earliest = Min(earliest, overlapStart);
                latest = Max(latest, overlapEnd);
                var slotWindow = slotWindows[slot];
                slotWindows[slot] = new(Min(slotWindow.Earliest, overlapStart), Max(slotWindow.Latest, overlapEnd));
            }
            result[kill] = new(earliest, latest);
        }
        return result;
    }

    private static bool CanAssign(int[] candidates, int[] exact, int kill, int used, int lateBudget, int skip = -1)
    {
        if (lateBudget < 0)
            return false;
        if (kill == candidates.Length)
            return true;
        if (kill == skip)
            return CanAssign(candidates, exact, kill + 1, used, lateBudget, skip);

        int available = candidates[kill] & ~used;
        while (available != 0)
        {
            int slot = available & -available;
            int remainingBudget = lateBudget - ((exact[kill] & slot) == 0 ? 1 : 0);
            if (CanAssign(candidates, exact, kill + 1, used | slot, remainingBudget, skip))
                return true;
            available ^= slot;
        }
        return false;
    }

    private void Invalidate(string reason)
    {
        _conflict = reason;
        _forecast = null;
        _waveReconciled = false;
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;

    private sealed class Kill(string name, RathuumTimeRange estimate, TimeSpan earliestLateDeath, TimeSpan confirmed)
    {
        internal string Name { get; } = name;
        internal TimeSpan Confirmed { get; } = confirmed;
        internal RathuumTimeRange Estimate { get; } = estimate;
        internal TimeSpan EarliestLateDeath { get; } = earliestLateDeath;
        internal TimeSpan? LastVisible { get; set; }
        internal TimeSpan? VisibleSince { get; set; }
        internal TimeSpan? FirstMissing { get; set; }
        internal RathuumTimeRange? Disappearance { get; set; }
        internal bool LateIdentityQualified { get; set; }
    }
}
