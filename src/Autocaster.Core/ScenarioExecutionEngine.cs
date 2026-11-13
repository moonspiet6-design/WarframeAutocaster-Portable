namespace Autocaster.Core;

public interface IWindowTarget
{
    bool TryArm();
    bool HasFocus();
    void VerifyPointer(InputEvent input);
}

public interface IRunLog
{
    void Write(string message, bool detailed = false);
}

public enum RunState { Idle, Armed, Preparing, Running, Paused, Stopping, Stopped, Error }
public sealed record RunStatus(RunState State, int Cycle = 0, ExecutionPlan? Plan = null, long Start = 0,
    int SentThrough = 0, TimingStats Stats = default, string? Message = null)
{
    public HudGate? WaitingForHud { get; init; }
    public RathuumWaveState? RathuumWave { get; init; }
    public long? RathuumLaunchAt { get; init; }
    public IndexRunHud? IndexHud { get; init; }
}

public sealed class ScenarioExecutionEngine(ScenarioCompiler compiler, ExecutionScheduler scheduler,
    InputOwnership ownership, IRunLog log)
{
    private readonly object _gate = new();
    private RunStatus _status = new(RunState.Idle);
    private Task _worker = Task.CompletedTask;
    private CancellationTokenSource? _segment;
    private bool _paused;
    private bool _stop;
    private string? _unreleased;

    public RunStatus Status => Volatile.Read(ref _status);
    public Task Completion { get { lock (_gate) return _worker; } }
    public bool HasUnreleasedInput { get { lock (_gate) return _unreleased is not null; } }
    public double MillisecondsSince(long start) => scheduler.MillisecondsSince(start);

    public bool Start(Func<ScenarioProfile> snapshot, IWindowTarget target, ScheduleOptions options, Action? startMacroTimer = null) =>
        Start(snapshot, target, options, startMacroTimer is null ? null : _ => startMacroTimer());

    public bool Start(Func<ScenarioProfile> snapshot, IWindowTarget target, ScheduleOptions options, Action<int?>? startMacroTimer)
    {
        lock (_gate)
        {
            if (!_worker.IsCompleted || _unreleased is not null)
                return false;
            _stop = _paused = false;
            Set(new(RunState.Armed));
            _worker = Task.Factory.StartNew(() => Run(snapshot, target, options, startMacroTimer),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return true;
        }
    }

    public void TogglePause()
    {
        lock (_gate)
        {
            if (_worker.IsCompleted || _stop)
                return;
            _paused = !_paused;
            if (_paused)
                _segment?.Cancel();
            else
                Monitor.PulseAll(_gate);
        }
    }

    public Task Stop()
    {
        lock (_gate)
        {
            if (_worker.IsCompleted)
            {
                if (_unreleased is not null)
                {
                    Set(Status with { State = RunState.Stopping });
                    _worker = Task.Run(() => Finish(null));
                }
                return _worker;
            }
            if (Status.State is RunState.Stopped or RunState.Error)
                return _worker;
            _stop = true;
            _segment?.Cancel();
            Set(Status with { State = RunState.Stopping });
            Monitor.PulseAll(_gate);
            return _worker;
        }
    }

    private void Run(Func<ScenarioProfile> snapshot, IWindowTarget target, ScheduleOptions options, Action<int?>? startMacroTimer = null)
    {
        int completed = 0;
        int indexRound = 1;
        bool needsFocus = true;
        bool readIndexOptions = false;
        IndexRoundSession? index = null;
        var releases = new TimedKeyReleases();
        var rathuumMonitors = new Dictionary<RathuumGateOptions, IRathuumMonitor>();

        IRathuumMonitor GetRathuumMonitor(RathuumGateOptions options)
        {
            // The scheduler owns the timeout. Share observations between gates that differ only in how long they wait.
            options = options with { TimeoutMs = new RathuumGateOptions().TimeoutMs };
            if (!rathuumMonitors.TryGetValue(options, out var monitor))
            {
                if (target is not IRathuumTarget rathuumTarget)
                    throw new ScenarioException("The current target cannot read the Rathuum kill feed.");
                monitor = rathuumTarget.StartRathuumMonitor(options);
                rathuumMonitors.Add(options, monitor);
            }
            return monitor;
        }

        void StopRathuumMonitors()
        {
            foreach (var monitor in rathuumMonitors.Values)
                monitor.Dispose();
            rathuumMonitors.Clear();
        }
        string? failure = null;
        string? finishMessage = null;

        try
        {
            while (true)
            {
                using var segment = new CancellationTokenSource();
                lock (_gate)
                {
                    while (_paused && !_stop)
                    {
                        Set(new(RunState.Paused, completed + 1) { IndexHud = index?.ReadHud(IndexHudPhase.Paused) });
                        Monitor.Wait(_gate);
                    }
                    if (_stop)
                        break;
                    _segment = segment;
                    Set(new(needsFocus ? RunState.Armed : RunState.Preparing, completed + 1)
                    {
                        IndexHud = index?.ReadHud(IndexHudPhase.Preparing)
                    });
                }

                var cancellation = segment.Token;
                try
                {
                    if (needsFocus)
                    {
                        while (!target.TryArm())
                        {
                            cancellation.ThrowIfCancellationRequested();
                            cancellation.WaitHandle.WaitOne(80);
                        }
                        needsFocus = false;
                    }
                    if (!target.HasFocus())
                        throw new TargetLostException();

                    var profile = snapshot();
                    if (!readIndexOptions && profile.Index is { WatchesRound: true } indexOptions)
                    {
                        indexOptions.Validate();
                        if (target is not IIndexTarget indexTarget)
                            throw new ScenarioException("The current target cannot read the Index screen.");
                        index = new(profile.Name, indexOptions, indexTarget, target, compiler, scheduler);
                    }
                    readIndexOptions = true;
                    var nokko = index?.Options.Nokko;
                    int cycle = nokko is null ? completed + 1 : indexRound;
                    int limit = CycleLimit(profile);

                    try
                    {
                        if (nokko is null && completed >= limit)
                        {
                            index?.AwaitVote(Report, cancellation);
                            break;
                        }

                        Publish(new(RunState.Preparing, cycle) { IndexHud = index?.ReadHud(IndexHudPhase.Preparing) }, cancellation);
                        log.Write($"Preparing config “{profile.Name}”, cycle {cycle}.");
                        var nokkoPlan = nokko is null ? null : index!.Compile(profile, nokko, cancellation);
                        bool hasNextCycle = nokko is not null || completed + 1 < limit;
                        var plan = nokkoPlan?.Plan ?? compiler.Compile(profile, hasNextCycle, cancellation);
                        if (plan.Events.IsEmpty && plan.DurationMs == 0 && plan.HudGates.IsEmpty &&
                            (startMacroTimer is null || plan.TimerTriggers.IsEmpty))
                        {
                            if (index is null)
                                throw new ScenarioException("The plan is empty.");
                            index.AwaitVote(Report, cancellation);
                        }
                        if (limit == int.MaxValue && plan.DurationMs == 0 && plan.HudGates.IsEmpty)
                            throw new ScenarioException("An infinite loop requires a nonzero cycle duration.");

                        foreach (var gate in plan.HudGates)
                            if (gate.Rathuum is { } rathuum)
                                GetRathuumMonitor(rathuum);

                        long start = scheduler.Timestamp;
                        var running = new RunStatus(RunState.Running, cycle, plan, start)
                        {
                            IndexHud = index?.ReadHud(IndexHudPhase.Running)
                        };
                        Publish(running, cancellation);
                        log.Write($"Starting cycle {cycle}, plan {plan.Id}, {plan.DurationMs / 1000d:0.###} s.");
                        scheduler.Execute(plan, start, options,
                            index is null ? target.HasFocus : () => index.CheckForVote(cancellation),
                            Dispatch, sample =>
                            {
                                index?.Observe(nokkoPlan, sample);
                                running = running with
                                {
                                    SentThrough = sample.Event.Number, Stats = running.Stats.Add(sample),
                                    IndexHud = index?.ReadHud(IndexHudPhase.Running)
                                };
                                Publish(running, cancellation);
                                if (sample.Error is not null)
                                    log.Write(sample.Error);
                                if (sample.Event.MinimumKeyIntervalMs.HasValue)
                                    log.Write($"Operator transfer: {sample.Event.Action}, {sample.Event.Input}, {sample.Result}, " +
                                        $"at {sample.ActualMs / 1000:F3} s, lateness {sample.LateMs / 1000:F3} s.");
                                if (log is IDetailedRunLog { Detailed: true })
                                    log.Write($"#{sample.Event.Number} {sample.Event.Input}: planned {sample.Event.OffsetMs / 1000d:0.###} s, sent at {sample.ActualMs / 1000:F6} s, Δ {sample.LateMs / 1000:F6} s, {sample.Result}.", true);
                            }, cancellation, releases, drainReleases: !hasNextCycle, startMacroTimer is null ? null : StartTimer,
                            target as ILarvaTarget, (gate, timelineStart) =>
                            {
                                running = running with { WaitingForHud = gate, Start = timelineStart, RathuumWave = null, RathuumLaunchAt = null };
                                Publish(running, cancellation);
                            }, GetRathuumMonitor, message => log.Write(message), state => UpdateRathuum(ref running, state));
                        log.Write($"Cycle {cycle} complete. Mean lateness {running.Stats.Mean / 1000:F6} s, maximum {running.Stats.Max / 1000:F6} s.");
                        if (nokko is null)
                            completed++;
                        if (!hasNextCycle && index is null)
                            break;
                    }
                    catch (IndexScreenChangedException change) when (index is not null)
                    {
                        Publish(new(RunState.Preparing, cycle) { IndexHud = index.ReadHud(IndexHudPhase.Vote) }, cancellation);
                        releases.Clear();
                        ReleaseForRestart();
                        finishMessage = index.HandleInterruption(change.Screen, Dispatch, Report, cancellation, out bool roundEnded);
                        if (finishMessage is not null)
                        {
                            log.Write(finishMessage);
                            break;
                        }
                        if (roundEnded)
                            indexRound++;
                    }

                    void UpdateRathuum(ref RunStatus status, RathuumGateReading reading)
                    {
                        var state = reading.State;
                        bool changed = status.RathuumWave != state;
                        if (!changed && status.RathuumLaunchAt == reading.LaunchAt)
                            return;
                        status = status with { RathuumWave = state, RathuumLaunchAt = reading.LaunchAt };
                        Publish(status, cancellation);
                        if (changed)
                        {
                            string launch = reading.LaunchAt is long deadline
                                ? $" Launch in {Math.Max(0, -scheduler.MillisecondsSince(deadline)) / 1000:0.000} s." : "";
                            log.Write($"Rathuum {state.Status}: baseline {state.StartingScore?.ToString() ?? "unread"}, " +
                                $"score {state.Score?.ToString() ?? "unread"}, named kills {state.NamedKills}. {state.Reason}{launch}");
                        }
                    }

                    void Dispatch(InputEvent input)
                    {
                        lock (_gate)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (!target.HasFocus())
                                throw new TargetLostException();
                            target.VerifyPointer(input);
                            ownership.Dispatch(input);
                        }
                    }

                    void StartTimer(int? duration)
                    {
                        lock (_gate)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (!target.HasFocus())
                                throw new TargetLostException();
                            startMacroTimer!(duration);
                        }
                    }

                    void Report(string message)
                    {
                        log.Write(message);
                        Publish(new(RunState.Preparing, cycle, Message: message)
                        {
                            IndexHud = index?.ReadHud(IndexHudPhase.Preparing)
                        }, cancellation);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    needsFocus = true;
                    StopRathuumMonitors();
                    index?.Restart();
                    releases.Clear();
                    ReleaseForRestart();
                }
                catch (TargetLostException error)
                {
                    log.Write(error.Message);
                    finishMessage = error.Message;
                    break;
                }
                finally
                {
                    lock (_gate)
                        _segment = null;
                }
            }
        }
        catch (Exception error)
        {
            failure = error.Message;
            log.Write(error.ToString());
        }
        finally
        {
            try { StopRathuumMonitors(); }
            finally { Finish(failure, finishMessage); }
        }
    }

    private void ReleaseForRestart()
    {
        if (ownership.ReleaseAll() is { } failure)
            throw new InvalidOperationException(failure);
    }

    private static int CycleLimit(ScenarioProfile profile)
    {
        if (profile.Actions.FirstOrDefault(action => action.Kind == ActionKind.Loop) is { } loop)
            return loop.LoopCount == 0 ? int.MaxValue : loop.LoopCount;
        return profile.Mode switch
        {
            CycleMode.Once => 1,
            CycleMode.Count => profile.CycleCount,
            _ => int.MaxValue
        };
    }

    private void Finish(string? failure, string? finishMessage = null)
    {
        lock (_gate)
        {
            _segment = null;
            _unreleased = ownership.ReleaseAll();
            failure = failure is null ? _unreleased : _unreleased is null ? failure : $"{failure} | {_unreleased}";
            if (failure is not null)
                log.Write(failure);
            Set(Status with { State = failure is null ? RunState.Stopped : RunState.Error,
                Message = failure ?? finishMessage, WaitingForHud = null, RathuumWave = null, RathuumLaunchAt = null });
        }
    }

    private void Publish(RunStatus status, CancellationToken cancellation)
    {
        lock (_gate)
        {
            cancellation.ThrowIfCancellationRequested();
            Set(status);
        }
    }

    private void Set(RunStatus status) => Volatile.Write(ref _status, status);
}

public interface IDetailedRunLog : IRunLog
{
    bool Detailed { get; }
}
