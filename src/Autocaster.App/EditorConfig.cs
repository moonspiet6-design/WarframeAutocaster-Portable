using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Autocaster.Core;

namespace Autocaster.App;

internal static class EditorConfig
{
    internal const int RandomStepMs = 10;
    internal static Timing KeyPressHold => Timing.Range(15, 30, 1);

    internal static Timing DelayTiming(Timing timing) => timing with { StepMs = RandomStepMs };

    internal static ScenarioAction UpgradeMouseTiming(ScenarioAction action) =>
        action.MouseSpeed != 1 && !action.MousePath.IsEmpty
            ? action with { MouseDurationMs = MouseRoute.ForwardDurationMs(action), MouseSpeed = 1 }
            : action;

    internal static ScenarioProfile Upgrade(ScenarioProfile config)
    {
        var legacyHolds = EstimateLegacyHolds(config.Actions);
        var actions = ImmutableArray.CreateBuilder<ScenarioAction>();
        var mode = config.Mode;
        int cycleCount = config.CycleCount;
        var boundaries = new List<int>(config.Actions.Length + 1) { 0 };
        foreach (var action in config.Actions)
        {
            switch (action.Kind)
            {
                case ActionKind.MouseWheel or ActionKind.KeyUp:
                    break;
                case ActionKind.Loop:
                    mode = action.LoopCount == 0 ? CycleMode.Infinite : CycleMode.Count;
                    if (action.LoopCount > 0)
                        cycleCount = action.LoopCount;
                    break;
                case ActionKind.KeyDown:
                    Add(action with { Kind = ActionKind.KeyHold, Hold = Timing.Fixed(legacyHolds.GetValueOrDefault(action.Id, 80)) });
                    break;
                case ActionKind.Combination:
                    if (action.Enabled)
                    {
                        var hold = DelayTiming(action.Hold);
                        for (int i = 0; i < action.Keys.Length; i++)
                        {
                            var key = new ScenarioAction { Kind = ActionKind.KeyHold, Key = action.Keys[i], Hold = hold };
                            Add(i == 0 ? key with { Id = action.Id } : key);
                        }
                        Add(new() { Kind = ActionKind.Wait, Delay = hold });
                    }
                    break;
                case ActionKind.KeyPress:
                    Add(action with { Hold = KeyPressHold });
                    break;
                case ActionKind.Wait:
                    Add(action with { Enabled = true, Delay = DelayTiming(action.Enabled ? action.Delay : Timing.Fixed(0)) });
                    break;
                case ActionKind.KeyHold or ActionKind.MouseClick:
                    Add(action with { Hold = DelayTiming(action.Hold) });
                    break;
                case ActionKind.MouseMovement:
                    Add(UpgradeMouseTiming(action));
                    break;
                default:
                    Add(action);
                    break;
            }
            boundaries.Add(actions.Count);
        }

        string name = config.Name is "Профиль 1" or "Конфиг 1" ? "Config 1" : config.Name;
        var updated = config with
        {
            Name = name, Actions = actions.ToImmutable(), Mode = mode, CycleCount = cycleCount,
            ActionGroups = config.ActionGroups.Select(group => group with { Start = boundaries[group.Start],
                Count = boundaries[group.Start + group.Count] - boundaries[group.Start] }).ToImmutableArray(),
            BetweenCycles = DelayTiming(mode == CycleMode.Once ? Timing.Fixed(0) : config.BetweenCycles)
        };
        ScenarioCompiler.ValidateProfile(updated);
        return updated;

        void Add(ScenarioAction action)
        {
            if (actions.Count >= ScenarioCompiler.MaxOperations)
                throw new ScenarioException("Too many rows after converting legacy actions.");
            actions.Add(action);
        }
    }

    private static Dictionary<Guid, int> EstimateLegacyHolds(ImmutableArray<ScenarioAction> actions)
    {
        var held = new Dictionary<InputKey, (Guid Id, long Start)>();
        var durations = new Dictionary<Guid, int>();
        long offset = 0;
        foreach (var action in actions)
        {
            if (!action.Enabled)
                continue;
            switch (action.Kind)
            {
                case ActionKind.KeyDown:
                    held.TryAdd(action.Key, (action.Id, offset));
                    break;
                case ActionKind.KeyUp:
                    if (held.Remove(action.Key, out var pressed))
                        Record(pressed);
                    break;
                case ActionKind.Wait:
                    offset += Nominal(action.Delay);
                    break;
                case ActionKind.KeyPress or ActionKind.MouseClick or ActionKind.Combination:
                    offset += Nominal(action.Hold);
                    break;
            }
        }
        foreach (var pressed in held.Values)
            Record(pressed);
        return durations;

        void Record((Guid Id, long Start) pressed) =>
            durations[pressed.Id] = (int)Math.Min(offset - pressed.Start, TimingEngine.MaxMilliseconds);
    }

    private static int Nominal(Timing timing) => timing.Random ? (timing.MinMs + timing.MaxMs) / 2 : timing.FixedMs;
}
