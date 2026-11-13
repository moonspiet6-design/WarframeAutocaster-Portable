using System.Collections.Immutable;
using System.Globalization;

namespace Autocaster.Core;

public sealed class TimingEngine(Random? random = null)
{
    public const int MaxMilliseconds = 3_600_000;
    private readonly Random _random = random ?? Random.Shared;

    public static void Validate(Timing timing, int maximumMs = MaxMilliseconds)
    {
        if (timing.FixedMs < 0 || timing.FixedMs > maximumMs)
            throw new ScenarioException($"Duration must be between 0 and {Limit()} seconds.");
        if (!timing.Random)
            return;
        if (timing.MinMs < 0 || timing.MaxMs > maximumMs || timing.MaxMs < timing.MinMs)
            throw new ScenarioException($"The range must satisfy 0 ≤ minimum ≤ maximum ≤ {Limit()} seconds.");
        if (timing.StepMs <= 0)
            throw new ScenarioException("The random step must be positive.");

        string Limit() => (maximumMs / 1000m).ToString("#,0.###", CultureInfo.InvariantCulture);
    }

    internal ImmutableArray<MouseSample> VaryRoute(ImmutableArray<MouseSample> path) => MouseRoute.Vary(path, _random);

    public int Resolve(Timing timing, int maximumMs = MaxMilliseconds)
    {
        Validate(timing, maximumMs);
        if (!timing.Random)
            return timing.FixedMs;
        int slots = (timing.MaxMs - timing.MinMs) / timing.StepMs + 1;
        return timing.MinMs + _random.Next(slots) * timing.StepMs;
    }
}

public sealed class ScenarioCompiler(TimingEngine timing)
{
    public const int MaxDepth = 16;
    public const int MaxOperations = 25_000;
    public const int MaxEvents = 50_000;
    public const int MaxCycleMs = 86_400_000;
    public const int MaxRepeat = 10_000;

    public static void ValidateProfile(ScenarioProfile profile, CancellationToken cancellation = default)
    {
        if (profile.SchemaVersion != ScenarioProfile.CurrentSchema)
            throw new ScenarioException($"Config version {profile.SchemaVersion} is unsupported.");
        if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 150)
            throw new ScenarioException("Check the config ID and name (1–150 characters).");
        if (!Enum.IsDefined(profile.Mode) ||
            profile.CycleCount is < 1 or > MaxRepeat)
            throw new ScenarioException("Invalid execution mode or cycle count (1–10,000).");
        if (profile.Macro is { } macro && (!Enum.IsDefined(macro.Key) || (macro.Modifiers & ~15u) != 0))
            throw new ScenarioException("Invalid macro shortcut.");
        profile.Macro?.Timer?.Validate();
        if (profile.Macro?.StepHudPosition is { IsValid: false })
            throw new ScenarioException("Check the Step HUD position.");
        if (profile.Macro?.ArrowPosition is { IsValid: false })
            throw new ScenarioException("Check the HUD arrow position.");
        if (profile.Index is { } index)
        {
            if (profile.Macro is not null)
                throw new ScenarioException("Index options belong to a scenario, not a macro.");
            index.Validate();
        }
        TimingEngine.Validate(profile.BetweenCycles);
        ValidateActions(profile.Actions, 1, [], profile.Macro is not null, cancellation);
        ValidateGroups(profile);
        if (profile.Actions.Count(action => action.Kind == ActionKind.Loop) > 1)
            throw new ScenarioException("Use one Loop row per scenario.");
    }

    private static void ValidateGroups(ScenarioProfile profile)
    {
        if (profile.ActionGroups.IsDefault || profile.ActionGroups.Length > MaxOperations)
            throw new ScenarioException("Invalid action groups.");
        if (profile.ActionGroups.IsEmpty)
            return;
        var ids = profile.Actions.Select(action => action.Id).ToHashSet();
        int end = 0;
        foreach (var group in profile.ActionGroups)
        {
            if (group is null || group.Id == Guid.Empty || !ids.Add(group.Id) ||
                string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 30 ||
                group.Start < end || group.Start > profile.Actions.Length ||
                group.Count < 0 || group.Count > profile.Actions.Length - group.Start || group.Color is < 0 or > 3)
                throw new ScenarioException("Invalid action group name or range.");
            end = group.Start + group.Count;
        }
    }

    private static void ValidateActions(ImmutableArray<ScenarioAction> actions, int depth, HashSet<Guid> ids,
        bool macro, CancellationToken cancellation)
    {
        if (actions.IsDefault || depth > MaxDepth)
            throw new ScenarioException($"Invalid action list or nesting exceeds {MaxDepth} levels.");
        foreach (var action in actions)
        {
            cancellation.ThrowIfCancellationRequested();
            if (action is null || action.Id == Guid.Empty || !ids.Add(action.Id) || ids.Count > MaxOperations)
                throw new ScenarioException("Invalid or duplicate action ID, or the scenario is too large.");

            try
            {
                if (action.Kind == ActionKind.Loop && depth != 1)
                    throw new ScenarioException("Loop belongs in the main scenario list.");
                if (action.Kind == ActionKind.StartMacroTimer && !macro)
                    throw new ScenarioException("Start timer belongs in a macro.");
                ValidateAction(action);
            }
            catch (ScenarioException error)
            {
                throw new ScenarioException($"{Label(action)} — {error.Message}", action.Id);
            }

            if (action.IsGroup && !action.Children.IsEmpty)
                ValidateActions(action.Children, depth + 1, ids, macro, cancellation);
        }
    }

    public static void ValidateAction(ScenarioAction action)
    {
        if (!Enum.IsDefined(action.Kind) || action.Name is null || action.Name.Length > 150 ||
            action.Comment is null || action.Comment.Length > 4096 || action.Children.IsDefault ||
            (!action.IsGroup && !action.Children.IsEmpty))
            throw new ScenarioException("Invalid action type, metadata, or child list.");
        if (action.MacroTimerDurationMs is < 1 or > TimingEngine.MaxMilliseconds)
            throw new ScenarioException("Check the timer duration.");
        if (action.Kind == ActionKind.LarvaDetect && action.LarvaTimeoutMs is < HudGate.MinTimeoutMs or > HudGate.MaxTimeoutMs)
            throw new ScenarioException("Larva timeout must be between 0.1 and 30 seconds.");
        if (action.Kind == ActionKind.RathuumKillFeed)
            action.Rathuum?.Validate();

        TimingEngine.Validate(action.Before);
        TimingEngine.Validate(action.After);

        switch (action.Kind)
        {
            case ActionKind.KeyPress or ActionKind.KeyDown or ActionKind.KeyUp or ActionKind.KeyHold when !InputKeys.IsKeyboard(action.Key):
                throw new ScenarioException("Select a supported key.");
            case ActionKind.Combination when action.Keys.IsDefaultOrEmpty || action.Keys.Length > 8 ||
                 action.Keys.Any(key => !InputKeys.IsKeyboard(key)) || action.Keys.Distinct().Count() != action.Keys.Length:
                throw new ScenarioException("A legacy chord must contain 1 to 8 distinct keys.");
            case ActionKind.MouseClick when !Enum.IsDefined(action.Button):
                throw new ScenarioException("Select a supported mouse button.");
            case ActionKind.MouseWheel when action.WheelDelta == 0 || Math.Abs((long)action.WheelDelta) > 12_000:
                throw new ScenarioException("Scroll delta must be nonzero and between -12,000 and 12,000.");
            case ActionKind.Loop when !action.Enabled || action.LoopCount is < 0 or > MaxRepeat:
                throw new ScenarioException("Invalid loop count or disabled Loop row.");
            case ActionKind.Repeat when !Enum.IsDefined(action.RepeatMode) || action.RepeatCount is < 1 or > MaxRepeat:
                throw new ScenarioException("Repeat count must be between 1 and 10,000.");
        }

        if (action.Kind is ActionKind.KeyPress or ActionKind.Combination or ActionKind.MouseClick or ActionKind.KeyHold)
            TimingEngine.Validate(action.Hold);
        if (action.Kind == ActionKind.Wait)
            TimingEngine.Validate(action.Delay);
        if (action.Kind == ActionKind.MouseMovement)
        {
            if (action.MousePointLimit is < MouseRouteBuffer.MinPointLimit or > MouseRouteBuffer.MaxPointLimit)
                throw new ScenarioException("The mouse point limit is out of range.", action.Id);
            if (action.MouseDurationMs is < 1 or > TimingEngine.MaxMilliseconds)
                throw new ScenarioException("Mouse movement time must be between 0.001 and 3,600 seconds.", action.Id);
            if (action.MouseSpeed is < 0.25m or > 4m)
                throw new ScenarioException("The legacy mouse speed is out of range.", action.Id);
            MouseRoute.Validate(action.MousePath, action.Id);
        }
    }


    public ExecutionPlan Compile(ScenarioProfile profile, bool includeCycleGap = false, CancellationToken cancellation = default)
    {
        ValidateProfile(profile, cancellation);
        var plan = new PlanBuilder(timing, cancellation);
        plan.Expand(profile.Actions);
        return plan.Finish(profile, includeCycleGap);
    }

    internal int Resolve(Timing value, int maximumMs) => timing.Resolve(value, maximumMs);

    public ExecutionPlan PreviewNokko(ScenarioProfile profile)
    {
        var options = profile.Index?.Nokko ?? throw new ScenarioException("This config has no Nokko rotation.");
        return CompileNokko(profile, options, 0, null, CancellationToken.None).Plan;
    }

    internal NokkoPlan CompileNokko(ScenarioProfile profile, NokkoOptions options, long start,
        NokkoProgress? previous, CancellationToken cancellation) =>
        NokkoPlanner.Build(profile, options, timing, this, start, previous, cancellation);

    public static string Label(ScenarioAction action) =>
        string.IsNullOrWhiteSpace(action.Name) ? action.Kind.ToString() : action.Name;

    private sealed class PlanBuilder(TimingEngine timing, CancellationToken cancellation)
    {
        private readonly ImmutableArray<ScheduledInput>.Builder _events = ImmutableArray.CreateBuilder<ScheduledInput>();
        private readonly ImmutableArray<PlannedAction>.Builder _actions = ImmutableArray.CreateBuilder<PlannedAction>();
        private readonly ImmutableArray<ResolvedTiming>.Builder _timings = ImmutableArray.CreateBuilder<ResolvedTiming>();
        private readonly List<(InputEvent Input, ScenarioAction Action)> _held = [];
        private readonly ImmutableArray<string>.Builder _notes = ImmutableArray.CreateBuilder<string>();
        private readonly ImmutableArray<MacroTimerTrigger>.Builder _timerTriggers = ImmutableArray.CreateBuilder<MacroTimerTrigger>();
        private readonly ImmutableArray<HudGate>.Builder _hudGates = ImmutableArray.CreateBuilder<HudGate>();
        private int _offset;
        private int _operations;

        public void Expand(ImmutableArray<ScenarioAction> actions)
        {
            foreach (var action in actions)
            {
                CountOperation();
                if (!action.Enabled)
                    continue;

                int start = _offset;
                AddTime(action.Before, action, "Before");
                switch (action.Kind)
                {
                    case ActionKind.KeyPress:
                        Hold(action, [KeyDown(action.Key)]);
                        break;
                    case ActionKind.KeyHold:
                        Emit(KeyDown(action.Key), action, timedHoldMs: ResolveTime(action.Hold, action, "Release after"));
                        break;
                    case ActionKind.KeyDown:
                        Down(KeyDown(action.Key), action);
                        break;
                    case ActionKind.KeyUp:
                        Up(KeyDown(action.Key), action);
                        break;
                    case ActionKind.Wait:
                        AddTime(action.Delay, action, "Delay");
                        break;
                    case ActionKind.Combination:
                        Hold(action, action.Keys.Select(KeyDown).ToArray());
                        break;
                    case ActionKind.MouseClick:
                        Hold(action, [new(InputKind.MouseButtonDown, (int)action.Button)]);
                        break;
                    case ActionKind.MouseMovement:
                        MoveMouse(action);
                        break;
                    case ActionKind.StartMacroTimer:
                        _timerTriggers.Add(new(_offset, _events.Count, action.MacroTimerDurationMs) { Order = _operations });
                        break;
                    case ActionKind.LarvaDetect:
                        _hudGates.Add(new(action.Id, _offset, _events.Count, action.LarvaTimeoutMs ?? HudGate.DefaultTimeoutMs)
                            { Order = _operations });
                        break;
                    case ActionKind.RathuumKillFeed:
                        var rathuum = action.Rathuum ?? new();
                        _hudGates.Add(new(action.Id, _offset, _events.Count, rathuum.TimeoutMs)
                            { Order = _operations, Rathuum = rathuum });
                        break;
                    case ActionKind.MouseWheel:
                        Emit(new(InputKind.MouseWheel, action.WheelDelta), action);
                        break;
                    case ActionKind.Sequence:
                        Expand(action.Children);
                        break;
                    case ActionKind.Repeat:
                        int count = action.RepeatMode == RepeatMode.Count ? action.RepeatCount : 1;
                        for (int i = 0; i < count; i++)
                        {
                            CountOperation();
                            Expand(action.Children);
                        }
                        break;
                }
                AddTime(action.After, action, "After");
                if (!action.IsGroup)
                    _actions.Add(new(action.Id, start, _offset));
                if (action.Kind == ActionKind.Loop)
                    break;
            }
        }

        public ExecutionPlan Finish(ScenarioProfile profile, bool includeCycleGap)
        {
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                var (input, action) = _held[i];
                _notes.Add($"{input.Parameter} is released at the end of the cycle.");
                Emit(input.Release, action, automaticRelease: true);
            }
            if (includeCycleGap)
                AddTime(profile.BetweenCycles, null, "Between cycles");

            if (_events.Count == 0 && _offset == 0 && _timerTriggers.Count == 0 && _hudGates.Count == 0)
                _notes.Add("The plan is empty. Add an action or a nonzero delay.");

            return new(Guid.NewGuid(), profile.Id, DateTimeOffset.UtcNow, _offset,
                _events.ToImmutable(), _timings.ToImmutable(), _notes.ToImmutable(), _actions.ToImmutable())
            { TimerTriggers = _timerTriggers.ToImmutable(), HudGates = _hudGates.ToImmutable() };
        }

        private void CountOperation()
        {
            cancellation.ThrowIfCancellationRequested();
            if (++_operations > MaxOperations)
                throw new ScenarioException($"A cycle supports at most {MaxOperations} expanded operations.");
        }

        private static InputEvent KeyDown(InputKey key) => new(InputKind.KeyDown, (int)key);

        private void MoveMouse(ScenarioAction action)
        {
            if (action.MousePath.IsEmpty || action.MousePath.All(sample => sample.DeltaX == 0 && sample.DeltaY == 0))
                throw new ScenarioException("Record a route for the mouse movement before starting.", action.Id);

            int forward = MouseRoute.ForwardDurationMs(action);
            var path = MouseRoute.Expand(action.MousePath, forward, action.VaryMousePath);
            if (action.VaryMousePath)
                path = timing.VaryRoute(path);
            int start = _offset;
            int recorded = path[^1].OffsetMs;
            int reverseLead = Math.Min(path[0].OffsetMs, MouseRoute.IntervalMs);
            int recordedReturn = reverseLead + recorded - path[0].OffsetMs;
            int total = action.ReverseMousePath ? ReturnAt(recordedReturn) : forward;
            if (total > MaxCycleMs - start)
                throw new ScenarioException("A cycle cannot last more than 24 hours.", action.Id);

            foreach (var sample in path)
            {
                _offset = start + (action.MouseDurationMs is int requested
                    ? ScaleTime(sample.OffsetMs, recorded, requested)
                    : LegacyTime(sample.OffsetMs));
                Move(sample.DeltaX, sample.DeltaY);
            }
            if (action.ReverseMousePath)
            {
                for (int index = path.Length - 1; index >= 0; index--)
                {
                    var sample = path[index];
                    _offset = start + ReturnAt(reverseLead + recorded - sample.OffsetMs);
                    Move(-sample.DeltaX, -sample.DeltaY);
                }
            }

            int ReturnAt(int elapsed) => action.MouseDurationMs is int requested
                ? forward + ScaleTime(elapsed, recordedReturn, requested)
                : LegacyTime(recorded + elapsed);

            int LegacyTime(int offset) => (int)Math.Round(offset / action.MouseSpeed);

            static int ScaleTime(int offset, int length, int requested) =>
                (int)Math.Round((double)offset * requested / length);

            void Move(int x, int y)
            {
                cancellation.ThrowIfCancellationRequested();
                if (x != 0 || y != 0)
                    Emit(new(InputKind.MouseMove, x, y), action);
            }
        }

        private void Hold(ScenarioAction action, InputEvent[] inputs)
        {
            foreach (var input in inputs)
                Down(input, action);
            AddTime(action.Hold, action, "Hold duration");
            for (int i = inputs.Length - 1; i >= 0; i--)
                Up(inputs[i], action);
        }

        private void Down(InputEvent input, ScenarioAction action)
        {
            if (_held.Exists(item => item.Input == input))
                throw new ScenarioException($"{Label(action)} — {input.Parameter} is already held.", action.Id);
            _held.Add((input, action));
            Emit(input, action);
        }

        private void Up(InputEvent input, ScenarioAction action)
        {
            int index = _held.FindIndex(item => item.Input == input);
            if (index < 0)
                throw new ScenarioException($"{Label(action)} — {input.Parameter} has not been pressed.", action.Id);
            _held.RemoveAt(index);
            Emit(input.Release, action);
        }

        private void Emit(InputEvent input, ScenarioAction action, bool automaticRelease = false, int? timedHoldMs = null)
        {
            if (_events.Count >= MaxEvents)
                throw new ScenarioException($"A plan supports at most {MaxEvents} events.", action.Id);
            _events.Add(new(_events.Count + 1, _offset, input, action.Id, Label(action), automaticRelease) { TimedHoldMs = timedHoldMs });
        }

        private void AddTime(Timing value, ScenarioAction? action, string parameter)
        {
            int milliseconds = ResolveTime(value, action, parameter);
            if (milliseconds > MaxCycleMs - _offset)
                throw new ScenarioException("A cycle cannot last more than 24 hours.", action?.Id);
            _offset += milliseconds;
        }

        private int ResolveTime(Timing value, ScenarioAction? action, string parameter)
        {
            int milliseconds = timing.Resolve(value);
            if (value.Random || milliseconds > 0)
                _timings.Add(new(action?.Id ?? Guid.Empty, action is null ? "" : Label(action), parameter, milliseconds, value.Random));
            return milliseconds;
        }
    }
}
