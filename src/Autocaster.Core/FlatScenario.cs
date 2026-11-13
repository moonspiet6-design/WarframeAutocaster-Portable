using System.Collections.Immutable;

namespace Autocaster.Core;

public static class FlatScenario
{
    public static ScenarioProfile Convert(ScenarioProfile profile)
    {
        ScenarioCompiler.ValidateProfile(profile);
        if (profile.Actions.All(action => !action.IsGroup && !HasDelay(action.Before) && !HasDelay(action.After)))
            return profile;

        var actions = ImmutableArray.CreateBuilder<ScenarioAction>();
        var ids = new HashSet<Guid>();
        int expanded = 0;
        var boundaries = new int[profile.Actions.Length + 1];
        int index = 0;
        foreach (var action in profile.Actions)
        {
            Append(action, parentEnabled: true);
            boundaries[++index] = actions.Count;
        }
        return profile with
        {
            Actions = actions.ToImmutable(),
            ActionGroups = profile.ActionGroups.Select(group => group with
            {
                Start = boundaries[group.Start],
                Count = boundaries[group.Start + group.Count] - boundaries[group.Start]
            }).ToImmutableArray()
        };

        void Append(ScenarioAction action, bool parentEnabled)
        {
            if (++expanded > ScenarioCompiler.MaxOperations)
                throw new ScenarioException("The legacy config contains too many nested operations.");
            bool enabled = parentEnabled && action.Enabled;
            AddDelay(action.Before, enabled);
            if (action.IsGroup)
            {
                int count = action.Kind == ActionKind.Repeat && action.RepeatMode == RepeatMode.Count && enabled
                    ? action.RepeatCount : 1;
                if (!action.Children.IsEmpty)
                    for (int i = 0; i < count; i++)
                        foreach (var child in action.Children)
                            Append(child, enabled);
            }
            else
            {
                Add(action with { Enabled = enabled, Before = Timing.Fixed(0), After = Timing.Fixed(0) });
            }
            AddDelay(action.After, enabled);
        }

        void AddDelay(Timing delay, bool enabled)
        {
            if (HasDelay(delay))
                Add(new() { Kind = ActionKind.Wait, Delay = delay, Enabled = enabled });
        }

        void Add(ScenarioAction action)
        {
            if (actions.Count >= ScenarioCompiler.MaxOperations)
                throw new ScenarioException("The legacy config contains too many rows after expanding groups and delays.");
            while (!ids.Add(action.Id))
                action = action with { Id = Guid.NewGuid() };
            actions.Add(action);
        }
    }

    private static bool HasDelay(Timing delay) => delay.Random || delay.FixedMs != 0;
}
