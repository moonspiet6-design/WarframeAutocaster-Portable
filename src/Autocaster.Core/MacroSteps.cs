using System.Collections.Immutable;

namespace Autocaster.Core;

public readonly record struct MacroStepPosition(int Step, int Total);

public sealed class MacroSteps
{
    private sealed class Cursor(ScenarioProfile profile)
    {
        internal ImmutableArray<ScenarioAction> Source { get; } = profile.Actions;
        internal bool Step { get; } = profile.Macro?.Step == true;
        internal ImmutableArray<ScenarioAction> Enabled { get; } =
            profile.Actions.Where(action => action.Enabled).ToImmutableArray();
        internal ImmutableArray<int> Starts { get; private set; }
        internal int Next { get; set; }
        internal Dictionary<Guid, MouseMovementHint?> MovementHints { get; } = [];

        internal void SetStarts()
        {
            int leading = 0;
            while (leading < Enabled.Length && Enabled[leading].Kind == ActionKind.Wait)
                leading++;
            Starts = Enumerable.Range(0, Enabled.Length)
                .Where(i => i == 0 || (Step && i > leading && !Continues(Enabled[i], Enabled[i - 1].Kind)))
                .ToImmutableArray();
        }
    }

    private readonly Dictionary<Guid, Cursor> _positions = [];

    public static bool Continues(ScenarioAction action, ActionKind? previous) =>
        action.RunWithPrevious || action.Kind == ActionKind.Wait || ScenarioAction.IsHudGate(action.Kind) ||
        (previous is { } kind && ScenarioAction.IsHudGate(kind));

    public void Forget(Guid id) => _positions.Remove(id);

    public void ResetAll() => _positions.Clear();

    public MacroStepPosition Position(ScenarioProfile profile)
    {
        var cursor = GetCursor(profile);
        return cursor.Enabled.IsEmpty ? default :
            cursor.Step ? new(cursor.Next + 1, cursor.Starts.Length) : new(1, cursor.Enabled.Length);
    }

    public ScenarioProfile Plan(ScenarioProfile profile)
    {
        var cursor = GetCursor(profile);
        var actions = cursor.Enabled;
        if (cursor.Step && !cursor.Starts.IsEmpty)
        {
            int start = cursor.Starts[cursor.Next];
            int end = cursor.Next + 1 < cursor.Starts.Length ? cursor.Starts[cursor.Next + 1] : actions.Length;
            actions = actions[start..end];
        }
        return profile with { Actions = actions, ActionGroups = [], Mode = CycleMode.Once, BetweenCycles = Timing.Fixed(0) };
    }

    public void Advance(ScenarioProfile profile)
    {
        var cursor = GetCursor(profile);
        if (cursor.Step && !cursor.Starts.IsEmpty)
            cursor.Next = (cursor.Next + 1) % cursor.Starts.Length;
    }

    public MouseMovementHint? NextMovement(ScenarioProfile profile, bool afterCurrentStep = false)
    {
        var cursor = GetCursor(profile);
        if (cursor.Enabled.IsEmpty)
            return null;
        int position = afterCurrentStep ? (cursor.Next + 1) % cursor.Starts.Length : cursor.Next;
        int start = cursor.Starts[position];
        for (int i = 0; i < cursor.Enabled.Length; i++)
        {
            var action = cursor.Enabled[(start + i) % cursor.Enabled.Length];
            if (!cursor.MovementHints.TryGetValue(action.Id, out var hint))
            {
                hint = MouseMovementHint.From(action);
                cursor.MovementHints.Add(action.Id, hint);
            }
            if (hint is not null)
                return hint;
        }
        return null;
    }

    private Cursor GetCursor(ScenarioProfile profile)
    {
        if (_positions.TryGetValue(profile.Id, out var cursor) && cursor.Source == profile.Actions &&
            cursor.Step == (profile.Macro?.Step == true))
            return cursor;

        cursor = new(profile);
        cursor.SetStarts();
        _positions[profile.Id] = cursor;
        return cursor;
    }
}
