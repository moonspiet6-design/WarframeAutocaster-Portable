using System.Collections.Immutable;
using Autocaster.Core;

namespace Autocaster.Tests;

public sealed class CompilerTests
{
    [Fact]
    public void FixedZeroAndInclusiveHundredMillisecondSteps()
    {
        var engine = new TimingEngine(new EdgeRandom());
        Assert.Equal(0, engine.Resolve(Timing.Fixed(0)));
        Assert.Equal(2500, engine.Resolve(Timing.Fixed(2500)));
        var range = Timing.Range(200, 500, 100);
        Assert.Equal(200, engine.Resolve(range));
        Assert.Equal(500, engine.Resolve(range));
        Assert.Equal(85, engine.Resolve(Timing.Range(85, 85, 1)));
    }

    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(200, 100, 100)]
    [InlineData(0, 200, 0)]
    [InlineData(0, int.MaxValue, 1)]
    public void InvalidRandomRangesAreRejected(int min, int max, int step) =>
        Assert.Throws<ScenarioException>(() => new TimingEngine().Resolve(Timing.Range(min, max, step)));

    [Fact]
    public void RandomRangeUsesTenMillisecondStepsWithoutExceedingAnUnalignedMaximum()
    {
        var engine = new TimingEngine(new EdgeRandom());
        var range = Timing.Range(15, 42, 10);
        Assert.Equal(15, engine.Resolve(range));
        Assert.Equal(35, engine.Resolve(range));
    }

    [Fact]
    public void TimelineHighlightsDelaysAndDoesNotIncludeTheLoopGap()
    {
        var delay = new ScenarioAction { Kind = ActionKind.Wait, Delay = Timing.Fixed(100) };
        var key = new ScenarioAction { Kind = ActionKind.KeyPress, Hold = Timing.Fixed(25) };
        var plan = new ScenarioCompiler(new()).Compile(Profile(delay, key), includeCycleGap: true);
        Assert.Equal(delay.Id, plan.ActionAt(50));
        Assert.Equal(key.Id, plan.ActionAt(100));
        Assert.Null(plan.ActionAt(125));
    }

    [Fact]
    public void ALoopInTheMiddleCompilesOnlyTheRowsBeforeIt()
    {
        var before = new ScenarioAction { Kind = ActionKind.KeyPress, Key = InputKey.A, Hold = Timing.Fixed(100) };
        var loop = new ScenarioAction { Kind = ActionKind.Loop };
        var after = new ScenarioAction { Kind = ActionKind.KeyPress, Key = InputKey.B, Hold = Timing.Fixed(200) };
        var profile = Profile(before, loop, after);
        var plan = new ScenarioCompiler(new()).Compile(profile);
        Assert.Equal(100, plan.DurationMs);
        Assert.Equal([before.Id, loop.Id], plan.Actions.Select(action => action.Id));
        Assert.All(plan.Events, item => Assert.Equal("A", item.Parameter));
        Assert.Equal(3, profile.Actions.Length);
    }

    [Fact]
    public void FullyExpandsNestedGroupsAndPreservesTieOrder()
    {
        var press = new ScenarioAction { Kind = ActionKind.KeyPress, Key = InputKey.D2, Hold = Timing.Fixed(30) };
        var group = new ScenarioAction
        {
            Kind = ActionKind.Repeat, RepeatCount = 2,
            Children = [new() { Kind = ActionKind.Sequence, Children = [press] }]
        };
        var profile = Profile(
            new() { Kind = ActionKind.KeyDown, Key = InputKey.LeftCtrl },
            group,
            new() { Kind = ActionKind.KeyUp, Key = InputKey.LeftCtrl },
            new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(100) });

        var plan = new ScenarioCompiler(new()).Compile(profile);
        Assert.Equal([0, 0, 30, 30, 60, 60], plan.Events.Select(e => e.OffsetMs));
        Assert.Equal([InputKind.KeyDown, InputKind.KeyDown, InputKind.KeyUp, InputKind.KeyDown, InputKind.KeyUp, InputKind.KeyUp],
            plan.Events.Select(e => e.Type));
        Assert.Equal(160, plan.DurationMs);
    }

    [Fact]
    public void CombinationReleasesInReverseOrderAndFinalExplicitHoldIsReleased()
    {
        var compiler = new ScenarioCompiler(new());
        var plan = compiler.Compile(Profile(
            new() { Kind = ActionKind.KeyDown, Key = InputKey.D0 },
            new() { Kind = ActionKind.Combination, Keys = [InputKey.RightCtrl, InputKey.LeftShift, InputKey.D1] }));
        Assert.Equal(["D0", "RightCtrl", "LeftShift", "D1", "D1", "LeftShift", "RightCtrl", "D0"],
            plan.Events.Select(e => e.Parameter));
        Assert.True(plan.Events[^1].AutomaticRelease);
        Assert.Single(plan.Notes);
    }

    [Fact]
    public void NewCycleResamplesWithoutMutatingEarlierPlan()
    {
        var compiler = new ScenarioCompiler(new(new EdgeRandom()));
        var action = new ScenarioAction { Kind = ActionKind.KeyPress, Before = Timing.Range(200, 400, 100) };
        var profile = Profile(action);
        var first = compiler.Compile(profile);
        var second = compiler.Compile(profile);
        var edited = compiler.Compile(profile with { Actions = [action with { Before = Timing.Fixed(3) }] });
        Assert.Equal(200, first.Events[0].OffsetMs);
        Assert.Equal(400, second.Events[0].OffsetMs);
        Assert.Equal(3, edited.Events[0].OffsetMs);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Single(first.Timings, t => t.Random);
    }

    [Fact]
    public void RejectsOrphanKeyUpAndOverlappingOwnership()
    {
        var compiler = new ScenarioCompiler(new());
        var release = new ScenarioAction { Kind = ActionKind.KeyUp };
        Assert.Equal(release.Id, Assert.Throws<ScenarioException>(() => compiler.Compile(Profile(release))).ActionId);
        var duplicate = new ScenarioAction { Kind = ActionKind.KeyPress };
        Assert.Equal(duplicate.Id, Assert.Throws<ScenarioException>(() => compiler.Compile(
            Profile(new() { Kind = ActionKind.KeyDown }, duplicate))).ActionId);
    }

    [Fact]
    public void BoundedExpansionAndDuration()
    {
        var inner = new ScenarioAction
        {
            Kind = ActionKind.Repeat, RepeatCount = 10_000, Children = [new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(0) }]
        };
        var outer = new ScenarioAction { Kind = ActionKind.Repeat, RepeatCount = 10_000, Children = [inner] };
        var compiler = new ScenarioCompiler(new());
        Assert.Throws<ScenarioException>(() => compiler.Compile(Profile(outer)));
        var longWait = inner with { Children = [new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(TimingEngine.MaxMilliseconds) }] };
        Assert.Throws<ScenarioException>(() => compiler.Compile(Profile(longWait)));
    }

    [Fact]
    public void DisabledParentDoesNotExpandAndPerCycleRepeatRunsOnce()
    {
        var plan = new ScenarioCompiler(new()).Compile(Profile(
            new() { Kind = ActionKind.Sequence, Enabled = false, Children = [new() { Kind = ActionKind.KeyUp }] },
            new() { Kind = ActionKind.Repeat, RepeatMode = RepeatMode.OncePerCycle, RepeatCount = 50,
                Children = [new() { Kind = ActionKind.MouseClick }] }));
        Assert.Equal([InputKind.MouseButtonDown, InputKind.MouseButtonUp], plan.Events.Select(item => item.Type));
    }

    internal static ScenarioProfile Profile(params ImmutableArray<ScenarioAction> actions) =>
        ScenarioProfile.Create("test") with { Actions = actions };

    private sealed class EdgeRandom : Random
    {
        private bool _upper;
        public override int Next(int maxValue)
        {
            int index = _upper ? maxValue - 1 : 0;
            _upper = !_upper;
            return index;
        }
    }
}
