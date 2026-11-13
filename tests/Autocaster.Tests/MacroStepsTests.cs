using Autocaster.Core;

namespace Autocaster.Tests;

public sealed class MacroStepsTests
{
    [Theory]
    [InlineData(ActionKind.LarvaDetect)]
    [InlineData(ActionKind.RathuumKillFeed)]
    public void HudGateAndItsFollowingActionRunOnTheSameHotkeyPress(ActionKind kind)
    {
        var cast = new ScenarioAction { Kind = ActionKind.KeyPress, Key = InputKey.D2 };
        var gate = new ScenarioAction { Kind = kind };
        var detonate = cast with { Id = Guid.NewGuid() };
        var next = new ScenarioAction { Kind = ActionKind.KeyPress, Key = InputKey.D1 };
        var profile = ScenarioProfile.Create("Larva") with
        {
            Actions = [cast, gate, detonate, next],
            Macro = new(Step: true)
        };
        var steps = new MacroSteps();
        Assert.Equal(new MacroStepPosition(1, 2), steps.Position(profile));
        Assert.Equal<ScenarioAction>([cast, gate, detonate], steps.Plan(profile).Actions);
        steps.Advance(profile);
        Assert.Equal(next, Assert.Single(steps.Plan(profile).Actions));
    }

    [Fact]
    public void StepMacrosAdvanceIndependentlySkipDisabledActionsAndWrap()
    {
        var steps = new MacroSteps();
        var first = new ScenarioAction { Kind = ActionKind.KeyPress, Key = InputKey.A };
        var disabled = first with { Id = Guid.NewGuid(), Enabled = false };
        var last = first with { Id = Guid.NewGuid(), Key = InputKey.B };
        var macro = ScenarioProfile.Create("Step") with
        {
            Macro = new(Step: true), Actions = [first, disabled, last], Mode = CycleMode.Infinite
        };
        var other = macro with { Id = Guid.NewGuid() };
        Assert.Equal(first, Assert.Single(steps.Plan(macro).Actions));
        Assert.Equal(first, Assert.Single(steps.Plan(macro).Actions));
        steps.Advance(macro);
        Assert.Equal(last, Assert.Single(steps.Plan(macro).Actions));
        Assert.Equal(first, Assert.Single(steps.Plan(other).Actions));
        steps.Advance(macro);
        Assert.Equal(first, Assert.Single(steps.Plan(macro).Actions));
        Assert.Equal(last, Assert.Single(steps.Plan(macro with { Actions = [last, first] }).Actions));

        var full = steps.Plan(macro with { Macro = new() });
        Assert.Equal(CycleMode.Once, full.Mode);
        Assert.Equal<ScenarioAction>([first, last], full.Actions);
    }

    [Fact]
    public void ChainsAutomaticActionsThenResetsEveryMacroTogether()
    {
        var steps = new MacroSteps();
        var first = new ScenarioAction { Kind = ActionKind.KeyPress, RunWithPrevious = true };
        var disabled = new ScenarioAction { Kind = ActionKind.KeyPress, Enabled = false };
        var wait = new ScenarioAction { Kind = ActionKind.Wait };
        var auto = new ScenarioAction { Kind = ActionKind.KeyPress, RunWithPrevious = true };
        var second = new ScenarioAction { Kind = ActionKind.KeyPress };
        var macro = ScenarioProfile.Create("Chain") with { Macro = new(Step: true), Actions = [first, disabled, wait, auto, second] };
        var other = macro with { Id = Guid.NewGuid() };

        Assert.Equal(new MacroStepPosition(1, 2), steps.Position(macro));
        Assert.Equal<ScenarioAction>([first, wait, auto], steps.Plan(macro).Actions);
        steps.Advance(macro);
        steps.Advance(other);
        Assert.Equal(new MacroStepPosition(2, 2), steps.Position(macro));
        Assert.Equal(second, Assert.Single(steps.Plan(macro).Actions));
        steps.ResetAll();
        Assert.Equal(new MacroStepPosition(1, 2), steps.Position(macro));
        Assert.Equal(new MacroStepPosition(1, 2), steps.Position(other));
        Assert.Equal<ScenarioAction>([first, wait, auto], steps.Plan(other).Actions);
    }

    [Fact]
    public void LeadingDelaysRunWithTheFirstActionWithoutTakingTheirOwnStep()
    {
        var delay = new ScenarioAction { Kind = ActionKind.Wait, Delay = Timing.Fixed(150) };
        var first = new ScenarioAction { Kind = ActionKind.KeyPress };
        var second = first with { Id = Guid.NewGuid(), Key = InputKey.B };
        var macro = ScenarioProfile.Create("Leading delay") with { Macro = new(Step: true),
            Actions = [delay, first, second], ActionGroups = [new(Guid.NewGuid(), "Group 1", 0, 3)] };
        var steps = new MacroSteps();
        Assert.Equal(new MacroStepPosition(1, 2), steps.Position(macro));
        Assert.Equal<ScenarioAction>([delay, first], steps.Plan(macro).Actions);
        Assert.Empty(steps.Plan(macro).ActionGroups);
        steps.Advance(macro);
        Assert.Equal(second, Assert.Single(steps.Plan(macro).Actions));
    }

    [Fact]
    public void ArrowLooksAheadAndUsesTheOutboundDirection()
    {
        var steps = new MacroSteps();
        var first = new ScenarioAction { Kind = ActionKind.KeyPress };
        var right = new ScenarioAction { Kind = ActionKind.MouseMovement, MousePath = [new(8, 40, -10), new(16, 20, 10)], ReverseMousePath = true };
        var closed = new ScenarioAction { Kind = ActionKind.MouseMovement, MousePath = [new(8, 0, -12), new(16, 0, 12)] };
        var macro = ScenarioProfile.Create("Arrow") with { Macro = new(Step: true), Actions = [first, right, closed] };
        Assert.Equal(new MouseMovementHint(right.Id, 60, 0), steps.NextMovement(macro));
        steps.Advance(macro);
        Assert.Equal(right.Id, steps.NextMovement(macro)?.ActionId);
        steps.Advance(macro);
        var up = Assert.IsType<MouseMovementHint>(steps.NextMovement(macro));
        Assert.Equal(closed.Id, up.ActionId);
        Assert.Equal(-90d, up.AngleDegrees);
    }

    [Fact]
    public void TimersAreIndependentAndRestartWithTheirConfiguredPosition()
    {
        var clock = new ManualTime();
        var timers = new MacroHudTimers(clock);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var options = new MacroTimerOptions { DurationMs = 900, Position = new(.2m, .8m) };
        timers.Start(first, options);
        clock.Now = 300;
        timers.Start(second, options);
        Assert.Equal(new MacroCountdown(first, 600, 900, options.Position), timers.Read(first));
        Assert.Equal(900, timers.Read(second)?.RemainingMs);
        clock.Now = 900;
        Assert.Null(timers.Read(first));
        Assert.Equal(300, timers.Read(second)?.RemainingMs);
        timers.Start(first, options with { DurationMs = 1500 });
        Assert.Equal(1500, timers.Read(first)?.RemainingMs);
        timers.Stop(second);
        Assert.Null(timers.Read(second));
    }

    private sealed class ManualTime : TimeProvider
    {
        public long Now { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }

}
