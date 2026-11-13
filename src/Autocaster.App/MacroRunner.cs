using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autocaster.Core;
using Autocaster.Windows;

namespace Autocaster.App;

internal sealed record MacroHudSnapshot(Guid MacroId, string Name, MacroStepPosition Step, Guid? ActiveActionId,
    bool Running, MouseMovementHint? NextMovement, MacroCountdown? Countdown, HudAnchor? StepPosition = null,
    HudAnchor? ArrowPosition = null, HudGate? WaitingForHud = null, int? RathuumWaveSize = null, RathuumWaveState? RathuumWave = null,
    double? PreLarvaMs = null);

internal sealed class MacroRunner(IInputBackend input, IRunLog log, Action<string> report, Func<int>? readWaveSize = null)
{
    private sealed class Run(ScenarioProfile plan, MacroStepPosition position, ScenarioExecutionEngine engine, WindowsDeadlineClock clock, bool pointer)
    {
        internal Guid Id => Plan.Id;
        internal ScenarioProfile Plan { get; } = plan;
        internal MacroStepPosition Position { get; } = position;
        internal Dictionary<Guid, MouseMovementHint?> MovementHints { get; } = [];
        internal WindowsDeadlineClock Clock { get; } = clock;
        internal ScenarioExecutionEngine Engine { get; } = engine;
        internal bool Pointer { get; } = pointer;
        internal bool Canceled { get; set; }
        internal Task Completion { get; set; } = Task.CompletedTask;
    }

    private readonly Dictionary<Guid, Run> _runs = [];
    private readonly MacroSteps _steps = new();
    private readonly MacroHudTimers _timers = new();
    private volatile bool _usesPointer;
    private RathuumGateOptions? _desiredWarmup;
    private RathuumGateOptions? _warmupOptions;
    private RathuumLiveMonitor? _warmup;
    internal bool UsesPointer => _usesPointer;
    internal bool Active => _runs.Count > 0;
    internal Guid? LastTriggered { get; private set; }

    internal static bool NeedsPointer(ScenarioProfile profile) =>
        profile.Actions.Any(action => action.Enabled && action.Kind is ActionKind.MouseMovement or ActionKind.MouseClick);

    internal void ConfigureRathuumWarmup(RathuumGateOptions? options)
    {
        _desiredWarmup = options;
        RefreshRathuumWarmup();
    }

    private void RefreshRathuumWarmup()
    {
        if (Active || (_desiredWarmup, _warmupOptions) is ({ } desired, { } current) &&
            desired.CompactHud == current.CompactHud && desired.ScanHz == current.ScanHz && desired.FpsGuard == current.FpsGuard &&
            desired.FirstKillOnly == current.FirstKillOnly)
            return;
        if (_warmup is { } previous)
            _ = Task.Run(previous.Dispose);
        _warmupOptions = _desiredWarmup;
        _warmup = _warmupOptions is { } options ? new(options, readWaveSize, standby: true) : null;
    }

    internal void Trigger(ScenarioProfile macro, Preferences settings, bool scenarioUsesPointer, IWindowTarget? target = null)
    {
        if (macro.Macro?.Enabled != true || _runs.ContainsKey(macro.Id))
            return;
        target ??= new LarvaTarget(readWaveSize, _warmup);
        if (!target.TryArm())
            return;

        var plan = _steps.Plan(macro);
        var position = _steps.Position(macro);
        if (plan.Actions.IsEmpty)
        {
            report($"“{macro.Name}” has no enabled actions.");
            return;
        }
        bool pointer = NeedsPointer(plan);
        if (pointer && (scenarioUsesPointer || UsesPointer))
        {
            report($"“{macro.Name}”: another run is using the mouse.");
            return;
        }
        if (_runs.Count >= 16)
        {
            report("Too many macros are running.");
            return;
        }

        var clock = new WindowsDeadlineClock { PrecisionMicroseconds = settings.SpinMicroseconds };
        var engine = new ScenarioExecutionEngine(new(new()), new(clock), new(input), log);
        if (!engine.Start(() => plan, target, settings.Timing, duration => StartTimer(macro, duration)))
        {
            clock.Dispose();
            return;
        }
        var run = new Run(plan, position, engine, clock, pointer);
        _runs.Add(macro.Id, run);
        LastTriggered = macro.Id;
        _usesPointer |= pointer;
        run.Completion = Complete();

        async Task Complete()
        {
            try
            {
                await engine.Completion;
                var status = engine.Status;
                if (status.State == RunState.Error && status.Message is { } error)
                    report($"“{macro.Name}”: {error}");
                else if (!run.Canceled && status.State == RunState.Stopped && status.Message is null)
                    _steps.Advance(macro);
            }
            finally
            {
                RemoveIfReleased(run);
            }
        }
    }

    internal MacroHudSnapshot? ReadHud(ScenarioProfile macro)
    {
        if (macro.Macro is not { } options)
            return null;
        var countdown = options.ShowTimer ? _timers.Read(macro.Id) : null;
        if (countdown is { } remaining)
            countdown = remaining with
            {
                Position = options.Timer?.Position ?? remaining.Position,
                FontSize = options.Timer?.FontSize ?? MacroTimerOptions.DefaultFontSize
            };
        _runs.TryGetValue(macro.Id, out var run);
        if (run?.Canceled == true)
            run = null;
        if (run is null && (!options.Enabled || !(options.ShowStepHud || options.ShowNextMovement)) && countdown is null)
            return null;
        var status = run?.Engine.Status;
        var plan = status?.State == RunState.Running ? status.Plan : null;
        var waiting = status?.WaitingForHud;
        double elapsed = plan is null ? 0 : waiting is { } gate ? gate.OffsetMs - .001 : run!.Engine.MillisecondsSince(status!.Start);
        Guid? activeAction = waiting?.ActionId ?? plan?.ActionAt(elapsed);
        if (activeAction is null && plan is not null && status!.SentThrough > 0)
        {
            var last = plan.Events[status.SentThrough - 1];
            if (elapsed - last.OffsetMs < 40)
                activeAction = last.ActionId;
        }

        var position = options.Enabled && options.ShowStepHud
            ? run?.Position ?? _steps.Position(macro) : default;
        if (position.Total > 0 && run?.Plan.Macro?.Step == false && plan is not null && activeAction is Guid current)
        {
            for (int i = 0; i < plan.Actions.Length; i++)
                if (plan.Actions[i].Id == current)
                {
                    position = new(i + 1, plan.Actions.Length);
                    break;
                }
        }
        MouseMovementHint? movement = null;
        if (options.Enabled && options.ShowNextMovement)
        {
            movement = plan is null ? _steps.NextMovement(macro) : FindUnstartedMovement(run!, plan, elapsed);
            if (plan is not null && movement is null && options.Step)
                movement = _steps.NextMovement(macro, afterCurrentStep: true);
        }
        bool running = status?.State is RunState.Armed or RunState.Preparing or RunState.Running;
        int? waveSize = position.Total > 0 && options.ShowRathuumWaveSize
            ? macro.Actions.Any(action => action.Enabled && action.Rathuum?.FirstKillOnly == true) ? 1 : readWaveSize?.Invoke() ?? 5
            : null;
        double? preLarvaMs = waiting?.Rathuum?.FirstKillOnly == true && status?.RathuumLaunchAt is long deadline
            ? Math.Max(0, -run!.Engine.MillisecondsSince(deadline)) : null;
        return new(macro.Id, macro.Name, position, activeAction, running, movement, countdown, options.StepHudPosition,
            options.ArrowPosition, waiting, waveSize, status?.RathuumWave, preLarvaMs);
    }

    private static MouseMovementHint? FindUnstartedMovement(Run run, ExecutionPlan plan, double elapsed)
    {
        foreach (var planned in plan.Actions)
        {
            if (planned.StartMs <= elapsed)
                continue;
            if (!run.MovementHints.TryGetValue(planned.Id, out var hint))
            {
                var action = run.Plan.Actions.FirstOrDefault(item => item.Id == planned.Id);
                hint = action is null ? null : MouseMovementHint.From(action);
                run.MovementHints.Add(planned.Id, hint);
            }
            if (hint is not null)
                return hint;
        }
        return null;
    }

    private void StartTimer(ScenarioProfile macro, int? duration)
    {
        if (macro.Macro is { } options)
        {
            var timer = options.Timer ?? new();
            _timers.Start(macro.Id, duration is int milliseconds ? timer with { DurationMs = milliseconds } : timer);
        }
    }

    internal void StopTimer(Guid id) => _timers.Stop(id);

    internal Task ResetSteps()
    {
        _steps.ResetAll();
        return StopRuns(_runs.Values.Where(run => run.Plan.Macro?.Step == true).ToArray());
    }

    internal Task ResetPlayback()
    {
        _steps.ResetAll();
        LastTriggered = null;
        // Request cancellation before clearing the countdowns so an in-flight action cannot restart one.
        var stopped = StopAll();
        _timers.Clear();
        return stopped;
    }

    internal bool HasUnreleasedInput => _runs.Values.Any(run => run.Engine.HasUnreleasedInput);

    internal Task StopAll() => StopRuns(_runs.Values.ToArray());

    internal async Task Remove(Guid id)
    {
        if (_runs.TryGetValue(id, out var run))
            await StopRuns([run]);
        _steps.Forget(id);
        _timers.Stop(id);
    }

    private async Task StopRuns(Run[] runs)
    {
        foreach (var run in runs)
            run.Canceled = true;
        await Task.WhenAll(runs.Select(run => run.Engine.Stop()));
        await Task.WhenAll(runs.Select(run => run.Completion));
        foreach (var run in runs)
            RemoveIfReleased(run);
    }

    private void RemoveIfReleased(Run run)
    {
        if (!run.Engine.HasUnreleasedInput && _runs.TryGetValue(run.Id, out var current) && current == run)
        {
            _runs.Remove(run.Id);
            _usesPointer = _runs.Values.Any(item => item.Pointer);
            run.Clock.Dispose();
            RefreshRathuumWarmup();
        }
    }
}
