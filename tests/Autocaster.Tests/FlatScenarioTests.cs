using Autocaster.Core;
using static Autocaster.Tests.CompilerTests;

namespace Autocaster.Tests;

public sealed class FlatScenarioTests
{
    [Fact]
    public void StandaloneDelaysPreserveLegacyEventOrderAndTimings()
    {
        var action = new ScenarioAction
        {
            Kind = ActionKind.KeyPress,
            Key = InputKey.D1,
            Before = Timing.Range(200, 600, 100),
            Hold = Timing.Fixed(80),
            After = Timing.Fixed(25)
        };
        var original = Profile(new ScenarioAction
        {
            Kind = ActionKind.Repeat, RepeatCount = 3, Before = Timing.Fixed(12), After = Timing.Fixed(20),
            Children =
            [
                new() { Kind = ActionKind.Sequence, Children = [action] },
                new() { Kind = ActionKind.KeyPress, Enabled = false, Before = Timing.Range(100, 200, 10) }
            ]
        });
        var flat = FlatScenario.Convert(original);
        Assert.All(flat.Actions, item =>
        {
            Assert.False(item.IsGroup);
            Assert.Equal(Timing.Fixed(0), item.Before);
            Assert.Equal(Timing.Fixed(0), item.After);
        });
        Assert.Equal(flat.Actions.Length, flat.Actions.Select(item => item.Id).Distinct().Count());
        var oldPlan = new ScenarioCompiler(new(new Random(42))).Compile(original);
        var newPlan = new ScenarioCompiler(new(new Random(42))).Compile(flat);
        Assert.Equal(oldPlan.DurationMs, newPlan.DurationMs);
        Assert.Equal(oldPlan.Events.Select(item => (item.OffsetMs, item.Input)),
            newPlan.Events.Select(item => (item.OffsetMs, item.Input)));
    }

    [Fact]
    public void ConversionBoundsExpansionEvenWhenNestedGroupsContainNoActions()
    {
        var inner = new ScenarioAction { Kind = ActionKind.Repeat, RepeatCount = 10_000 };
        var middle = new ScenarioAction { Kind = ActionKind.Repeat, RepeatCount = 10_000, Children = [inner] };
        var outer = new ScenarioAction { Kind = ActionKind.Repeat, RepeatCount = 10_000, Children = [middle] };
        Assert.Throws<ScenarioException>(() => FlatScenario.Convert(Profile(outer)));
    }
}
