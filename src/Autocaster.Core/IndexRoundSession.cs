namespace Autocaster.Core;

internal sealed class IndexScreenChangedException(IndexScreen screen) : Exception
{
    internal IndexScreen Screen { get; } = screen;
}

internal sealed class IndexRoundSession(string name, IndexOptions options, IIndexTarget screen, IWindowTarget target,
    ScenarioCompiler compiler, ExecutionScheduler scheduler)
{
    private const int PollMs = 125;
    private const int MaxChoiceAttempts = 4;
    private readonly long _start = scheduler.Timestamp;
    private readonly int? _extractAfter = options.Vote == IndexVoteMode.ExtractOnTimer
        ? compiler.Resolve(options.ExtractAfter, IndexOptions.MaxExtractAfterMs) : null;
    private long _nextProbe;
    private IndexScreen _candidate;
    private int _matches;
    private NokkoProgress? _progress;
    private long _pausedMs;
    private long? _pauseStarted;
    private readonly IndexHudTimeline? _hud = options.Nokko is null ? null : new();
    private IndexHudPhase? _votePhase;
    private long? _hudFrozenAt;
    private long? _frozenOperatorDue;
    private bool _hudRestart;

    internal IndexOptions Options => options;
    private long Elapsed => (long)scheduler.MillisecondsSince(_start);

    internal bool CheckForVote(CancellationToken cancellation)
    {
        if (!target.HasFocus())
            return false;
        if (Elapsed >= _nextProbe)
        {
            long sampledAt = Elapsed;
            var observed = Read(cancellation);
            if (observed is IndexScreen.Vote or IndexScreen.Results)
            {
                _pauseStarted = sampledAt;
                FreezeHud(sampledAt);
                _votePhase = observed == IndexScreen.Vote ? VotePhase(sampledAt) : IndexHudPhase.Vote;
                throw new IndexScreenChangedException(observed);
            }
        }
        return true;
    }

    internal void AwaitVote(Action<string> report, CancellationToken cancellation)
    {
        report("Waiting for the next Index vote");
        while (CheckForVote(cancellation))
            scheduler.WaitMilliseconds(PollMs, cancellation);
        throw new TargetLostException();
    }

    internal string? HandleInterruption(IndexScreen observed, Action<InputEvent> dispatch,
        Action<string> report, CancellationToken cancellation, out bool roundEnded)
    {
        roundEnded = false;
        long pausedAt = _pauseStarted ?? Elapsed;
        long limit = Elapsed + 30_000;
        bool chosen = false;
        bool extract = false;
        long? voteAt = observed == IndexScreen.Vote ? pausedAt : null;
        long retryAt = 0;
        int attempts = 0;
        long? clearSince = null;
        string? lastMessage = null;

        while (Elapsed < limit)
        {
            var current = Read(cancellation);
            if (current == IndexScreen.Vote)
            {
                roundEnded |= _matches >= 2;
                voteAt ??= Elapsed;
                _votePhase = VotePhase(voteAt.Value);
            }
            if (current == IndexScreen.Results && _matches >= 2)
                return chosen && extract ? "Cash In selected. Index results detected." : "Index results detected.";
            if (current == IndexScreen.NoVote)
            {
                clearSince ??= Elapsed;
                if (Elapsed - clearSince >= 500)
                {
                    if (chosen && extract)
                        return "Cash In clicked. Scenario stopped.";
                    _pausedMs += Elapsed - pausedAt;
                    _pauseStarted = null;
                    return null;
                }
            }
            else
                clearSince = null;

            bool auto = options.Vote != IndexVoteMode.Manual;
            if (current == IndexScreen.Vote && _matches >= 2 && !chosen && auto &&
                attempts < MaxChoiceAttempts && Elapsed >= retryAt)
            {
                extract = _extractAfter is int after && voteAt >= after;
                if (attempts == 0)
                    report(extract ? "Index vote detected. Selecting Cash In" : "Index vote detected. Selecting Stay Invested");
                VerifyFocus(cancellation);
                if (screen.TryPointAtIndexChoice(extract))
                {
                    scheduler.WaitMilliseconds(80, cancellation);
                    if (Read(cancellation) == IndexScreen.Vote && screen.IsPointingAtIndexChoice())
                    {
                        dispatch(new(InputKind.MouseButtonDown, (int)MouseButton.Left));
                        scheduler.WaitMilliseconds(60, cancellation);
                        dispatch(new(InputKind.MouseButtonUp, (int)MouseButton.Left));
                        chosen = true;
                    }
                }
                attempts++;
                retryAt = Elapsed + 250;
            }

            string message = chosen ? (extract ? "Cash In clicked" : "Stay Invested clicked") :
                current != IndexScreen.Vote ? "Waiting for the vote screen to close" :
                !auto ? "Waiting for your Index choice" :
                attempts >= MaxChoiceAttempts ? "Could not safely reach the Index choice. Choose it manually." : "Waiting to click the Index choice";
            if (message != lastMessage)
                report(lastMessage = message);
            scheduler.WaitMilliseconds(PollMs, cancellation);
        }
        return chosen && extract ? "Cash In was clicked. Results could not be confirmed." :
            "The vote screen could not be cleared. The scenario stopped.";
    }

    internal NokkoPlan Compile(ScenarioProfile profile, NokkoOptions nokko, CancellationToken cancellation)
    {
        long started = Elapsed;
        long logicalStart = started - _pausedMs;
        var plan = compiler.CompileNokko(profile, nokko, logicalStart, _progress, cancellation);
        _progress ??= plan.Initial;
        _hud?.Prepare(plan, logicalStart, _hudRestart);
        long ready = Elapsed;
        _pausedMs += ready - started;
        _hud?.Start(ready);
        _hudRestart = false;
        _hudFrozenAt = _frozenOperatorDue = null;
        _votePhase = null;
        return plan;
    }

    internal void Observe(NokkoPlan? plan, EventSample sample)
    {
        if (plan is not null && sample.Result == SendResult.Sent && sample.Event.Type == InputKind.KeyDown &&
            plan.AfterPress.TryGetValue(sample.Event.ActionId, out var progress))
            _progress = progress;
        if (plan is not null)
            _hud?.Observe(sample);
    }

    internal void Restart()
    {
        FreezeHud(Elapsed);
        _votePhase = null;
        _hudRestart = true;
        _progress = null;
        _pauseStarted = null;
    }

    internal IndexRunHud? ReadHud(IndexHudPhase phase) => _hud is null ? null : new(name, _start, _extractAfter,
        _hud.Completed, _hud.Total, _hud.Action, _hudFrozenAt.HasValue ? _frozenOperatorDue : _hud.OperatorDue(_progress, _pausedMs),
        _progress?.Return is not null || (_hudFrozenAt.HasValue && _hud.Action == "Enter Operator"),
        _hudFrozenAt, _votePhase ?? phase);

    private void FreezeHud(long elapsed)
    {
        if (_hud is null || _hudFrozenAt.HasValue)
            return;
        _frozenOperatorDue = _hud.OperatorDue(_progress, _pausedMs);
        _hudFrozenAt = elapsed;
    }

    private IndexHudPhase VotePhase(long opened) => options.Vote == IndexVoteMode.Manual ? IndexHudPhase.Vote :
        _extractAfter is int after && opened >= after ? IndexHudPhase.CashIn : IndexHudPhase.StayInvested;

    private IndexScreen Read(CancellationToken cancellation)
    {
        VerifyFocus(cancellation);
        IndexScreen current = screen.ReadIndexScreen();
        _nextProbe = Elapsed + PollMs;
        _matches = current == _candidate ? Math.Min(_matches + 1, 2) : 1;
        _candidate = current;
        return current;
    }

    private void VerifyFocus(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!target.HasFocus())
            throw new TargetLostException();
    }
}
