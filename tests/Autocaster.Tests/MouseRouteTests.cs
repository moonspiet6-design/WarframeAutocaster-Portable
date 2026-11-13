using System.Collections.Immutable;
using Autocaster.Core;
using static Autocaster.Tests.CompilerTests;

namespace Autocaster.Tests;

public sealed class MouseRouteTests
{
    private static readonly ImmutableArray<MouseSample> Route = [new(8, 60, 5), new(100, 70, 15), new(130, 30, -8)];

    [Fact]
    public void AddingAndDeletingAPointPreservesRouteDurationAndDisplacement()
    {
        var inserted = MouseRoute.InsertPoint(Route, 1, .5);
        Assert.Equal(new MouseSample(54, 35, 8) { ControlPoint = true }, inserted[1]);
        Assert.Equal([8, 54, 100, 130], inserted.Select(point => point.OffsetMs));
        Assert.Equal<MouseSample>(Route, MouseRoute.RemovePoint(inserted, 1));
        Assert.Equal(inserted, MouseRoute.RemovePoint(inserted, inserted.Length - 1));

        var edited = MouseRoute.RemovePoint(inserted, 2);
        Assert.Equal(inserted[^1].OffsetMs, edited[^1].OffsetMs);
        Assert.Equal(inserted.Sum(point => point.DeltaX), edited.Sum(point => point.DeltaX));
        Assert.Equal(inserted.Sum(point => point.DeltaY), edited.Sum(point => point.DeltaY));
    }

    [Fact]
    public void RecordingCombinesPacketsAndKeepsPausesBetweenMovement()
    {
        var buffer = new MouseRouteBuffer();
        Assert.True(buffer.Add(0, 0, 0));
        Assert.True(buffer.Add(1000, 3, 10));
        Assert.True(buffer.Add(1007, 7, -1));
        Assert.True(buffer.Add(1032, -4, 6));
        Assert.False(buffer.Add(61_000, 50, 50));
        Assert.Equal<MouseSample>([new(8, 10, 9), new(40, -4, 6)], buffer.Snapshot());
    }

    [Fact]
    public void CompactRecordingKeepsTheTurnAndInterpolatesPlayback()
    {
        var raw = Enumerable.Range(1, 100)
            .Select(i => new MouseSample(i * 8, i <= 50 ? 10 : 0, i <= 50 ? 0 : 10)).ToImmutableArray();
        var compact = MouseRouteBuffer.Compact(raw, 20);
        Assert.True(compact.Length < 20);
        Assert.Contains(compact, sample => sample.OffsetMs == 400);
        Assert.Equal(800, compact[^1].OffsetMs);
        var movement = new ScenarioAction { Kind = ActionKind.MouseMovement, MousePath = compact, VaryMousePath = false,
            MouseDurationMs = 1600 };
        var plan = new ScenarioCompiler(new()).Compile(Profile(movement));
        Assert.Equal(1600, plan.DurationMs);
        Assert.Equal(500, plan.Events.Sum(item => item.Input.Value));
        Assert.Equal(500, plan.Events.Sum(item => item.Input.DeltaY));
        Assert.True(plan.Events.Length > 100);
    }

    [Fact]
    public void ReverseStartsPromptlyAndRetracesTheSameDeltas()
    {
        var movement = new ScenarioAction { Kind = ActionKind.MouseMovement, MousePath = Route, ReverseMousePath = true, VaryMousePath = false };
        var plan = new ScenarioCompiler(new()).Compile(Profile(movement, new() { Kind = ActionKind.KeyPress, Key = InputKey.A }));
        var moves = plan.Events.Where(item => item.Type == InputKind.MouseMove).ToArray();
        Assert.Equal([8, 100, 130, 138, 168, 260], moves.Select(item => item.OffsetMs));
        Assert.Equal([(60, 5), (70, 15), (30, -8), (-30, 8), (-70, -15), (-60, -5)],
            moves.Select(item => (item.Input.Value, item.Input.DeltaY)));
        Assert.Equal(260, plan.Events.First(item => item.Type == InputKind.KeyDown).OffsetMs);
        Assert.Equal(movement.Id, plan.ActionAt(200));
    }

    [Fact]
    public void VariedRoutePreservesTheEndpointAndReversesItsOwnChanges()
    {
        var action = new ScenarioAction { Kind = ActionKind.MouseMovement, MousePath = Route, ReverseMousePath = true };
        var plan = new ScenarioCompiler(new(new Random(12))).Compile(Profile(action));
        var forward = plan.Events.TakeWhile(item => item.OffsetMs <= Route[^1].OffsetMs)
            .Select(item => (X: item.Input.Value, Y: item.Input.DeltaY)).ToArray();
        var backward = plan.Events.Skip(forward.Length).Select(item => (item.Input.Value, item.Input.DeltaY));
        Assert.False(Route.Select(sample => (sample.DeltaX, sample.DeltaY)).SequenceEqual(forward));
        Assert.Equal(Route.Sum(sample => sample.DeltaX), forward.Sum(delta => delta.X));
        Assert.Equal(Route.Sum(sample => sample.DeltaY), forward.Sum(delta => delta.Y));
        Assert.Equal(forward.Reverse().Select(delta => (-delta.X, -delta.Y)), backward);
    }

    [Fact]
    public void LegacySpeedStillScalesBothPassesWithoutChangingMovement()
    {
        var movement = new ScenarioAction { Kind = ActionKind.MouseMovement, MousePath = Route,
            ReverseMousePath = true, VaryMousePath = false };
        var compiler = new ScenarioCompiler(new());
        var normal = compiler.Compile(Profile(movement));
        var fast = compiler.Compile(Profile(movement with { MouseSpeed = 2 }));
        Assert.Equal(normal.Events.Select(item => item.Input), fast.Events.Select(item => item.Input));
        Assert.Equal(normal.Events.Select(item => item.OffsetMs / 2), fast.Events.Select(item => item.OffsetMs));
    }

    [Theory]
    [InlineData(false, 337)]
    [InlineData(true, 674)]
    public void CustomDurationEndsEachPassOnTimeAndPreservesMovement(bool reverse, int expectedTotal)
    {
        var movement = new ScenarioAction
        {
            Kind = ActionKind.MouseMovement,
            MousePath = Route.SetItem(0, Route[0] with { OffsetMs = 12 }),
            ReverseMousePath = reverse, VaryMousePath = false
        };
        var key = new ScenarioAction { Kind = ActionKind.KeyPress, Key = InputKey.A };
        var compiler = new ScenarioCompiler(new());
        var normal = compiler.Compile(Profile(movement, key));
        var timed = compiler.Compile(Profile(movement with { MouseDurationMs = 337 }, key));
        Assert.Equal(normal.Events.Select(item => item.Input), timed.Events.Select(item => item.Input));
        Assert.Equal(337, timed.Events[Route.Length - 1].OffsetMs);
        Assert.Equal(expectedTotal, timed.Events.Last(item => item.Type == InputKind.MouseMove).OffsetMs);
        Assert.Equal(expectedTotal, timed.Events.First(item => item.Type == InputKind.KeyDown).OffsetMs);
    }

    [Fact]
    public void MissingOrInvalidRouteFailsBeforeDispatch()
    {
        var compiler = new ScenarioCompiler(new());
        var action = new ScenarioAction { Kind = ActionKind.MouseMovement };
        Assert.Equal(action.Id, Assert.Throws<ScenarioException>(() => compiler.Compile(Profile(action))).ActionId);
        var invalid = action with { MousePath = [new(8, 2, 3), new(8, 3, 2)] };
        Assert.Equal(invalid.Id, Assert.Throws<ScenarioException>(() => compiler.Compile(Profile(invalid))).ActionId);
    }
}
