using Autocaster.Core;
using Autocaster.Infrastructure;

namespace Autocaster.Tests;

public sealed class IndexScenarioTests
{
    private static readonly NokkoOptions FixedRotation = new()
    {
        CastGap = Timing.Fixed(700), MushroomLead = Timing.Fixed(2_500), RoarExtra = Timing.Fixed(1_500),
        FourthInterval = Timing.Fixed(800)
    };

    [Fact]
    public async Task PlansTheRotationBeforeItsFirstInputAndSchedulesIndependentTimers()
    {
        var run = await Execute(new() { Nokko = FixedRotation }, time =>
            time < 110_000 ? IndexScreen.NoVote : IndexScreen.Results);
        Assert.Equal(RunState.Stopped, run.Status.State);
        var presses = run.Keys;
        Assert.Equal(new[] { InputKey.D2, InputKey.D2, InputKey.D1, InputKey.D1, InputKey.D1, InputKey.D1, InputKey.D3, InputKey.D4 },
            presses.Take(8).Select(item => item.Key));
        Assert.NotNull(run.FirstPlan);
        Assert.True(run.FirstPlan.DurationMs >= 1_800_000);
        Assert.True(run.FirstPlan.Events.Length > 4_000);

        var mushrooms = presses.Where(item => item.Key == InputKey.D2).ToArray();
        Assert.Equal(29_380, mushrooms[2].At - mushrooms[0].At);
        var roar = presses.Where(item => item.Key == InputKey.D3).ToArray();
        Assert.InRange(roar[1].At - roar[0].At, 39_750, 40_450);
        Assert.InRange(roar[2].At - roar[1].At, 39_750, 40_450);
        var transfer = presses.Where(item => item.Key == InputKey.D5).ToArray();
        Assert.Equal(2, transfer.Length);
        Assert.InRange(transfer[0].At - presses[0].At, 100_000, 100_700);
        Assert.Equal(300, transfer[1].At - transfer[0].At);
        Assert.DoesNotContain(presses, item => item.At > transfer[0].At && item.At < transfer[1].At);
    }

    [Fact]
    public async Task EachMushroomAbilityUsesItsOwnDelayAfterCasting()
    {
        var run = await Execute(new()
        {
            Nokko = FixedRotation with
            {
                Mushroom2Gap = Timing.Fixed(420),
                Mushroom1Gap = Timing.Range(950, 1150, 10)
            }
        }, time => time < 8_000 ? IndexScreen.NoVote : IndexScreen.Results);
        var opening = run.Keys.Take(7).ToArray();
        Assert.Equal([InputKey.D2, InputKey.D2, InputKey.D1, InputKey.D1, InputKey.D1, InputKey.D1, InputKey.D3],
            opening.Select(press => press.Key));
        Assert.Equal(420, opening[1].At - opening[0].At);
        Assert.Equal(420, opening[2].At - opening[1].At);
        for (int i = 3; i < opening.Length; i++)
            Assert.InRange(opening[i].At - opening[i - 1].At, 950, 1150);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(350)]
    public async Task OperatorTapsKeepTheirActualHoldAndReturnGapWhenDispatchRunsLate(int waitOverrunMs)
    {
        var run = await Execute(new() { Nokko = FixedRotation },
            time => time < 110_000 ? IndexScreen.NoVote : IndexScreen.Results, waitOverrunMs: waitOverrunMs);
        Assert.Equal(RunState.Stopped, run.Status.State);
        var transfer = run.Events.Where(item => item.Event.Value == (int)InputKey.D5 &&
            item.Event.Kind is InputKind.KeyDown or InputKind.KeyUp).ToArray();
        Assert.Equal([InputKind.KeyDown, InputKind.KeyUp, InputKind.KeyDown, InputKind.KeyUp],
            transfer.Select(item => item.Event.Kind));
        Assert.True(transfer[1].At - transfer[0].At >= 80);
        Assert.True(transfer[2].At - transfer[1].At >= 220);
        Assert.True(transfer[3].At - transfer[2].At >= 80);
        Assert.Equal(4, run.Log.Count(message => message.StartsWith("Operator transfer:")));
        Assert.DoesNotContain(run.Log, message => message.Contains("Skipped") || message.Contains("Failed"));
    }

    [Fact]
    public async Task OperatorReturnsAcrossThePlanBoundaryAndAfterThreeHours()
    {
        const int interval = 89_990;
        var run = await Execute(new()
        {
            Nokko = FixedRotation with
            {
                Mushroom1Enabled = false, Mushroom2Enabled = false, RoarEnabled = false, FourthEnabled = false,
                OperatorInterval = Timing.Fixed(interval)
            }
        }, time => time < 10_810_000 ? IndexScreen.NoVote : IndexScreen.Results);
        var transfers = run.Keys.Where(item => item.Key == InputKey.D5).ToArray();
        Assert.Equal(240, transfers.Length);
        for (int i = 0; i < transfers.Length; i += 2)
        {
            Assert.Equal(interval * (i / 2 + 1), transfers[i].At);
            Assert.Equal(300, transfers[i + 1].At - transfers[i].At);
        }
        // The twentieth visit straddles the first thirty-minute plan boundary.
        Assert.Equal(1_799_800, transfers[38].At);
        Assert.Equal(1_800_100, transfers[39].At);
        Assert.Equal(RunState.Stopped, run.Status.State);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(300)]
    public async Task OperatorProtectionPreservesShortConfiguredStaysAndCustomKeys(int stayMs)
    {
        var run = await Execute(new()
        {
            Nokko = FixedRotation with { Operator = InputKey.F6, OperatorStay = Timing.Fixed(stayMs) }
        }, time => time < 110_000 ? IndexScreen.NoVote : IndexScreen.Results);
        var transfer = run.Keys.Where(item => item.Key == InputKey.F6).ToArray();
        Assert.Equal(2, transfer.Length);
        Assert.Equal(stayMs, transfer[1].At - transfer[0].At);
        Assert.Equal(4, run.Log.Count(message => message.StartsWith("Operator transfer:")));
        Assert.Equal(RunState.Stopped, run.Status.State);
    }

    [Fact]
    public async Task VoteBetweenOperatorEntryAndReturnKeepsThePendingReturn()
    {
        var run = await Execute(new()
        {
            Vote = IndexVoteMode.Stay,
            Nokko = FixedRotation with { OperatorInterval = Timing.Fixed(8_000) }
        }, time => time switch
        {
            < 8_100 => IndexScreen.NoVote,
            < 10_000 => IndexScreen.Vote,
            < 12_000 => IndexScreen.NoVote,
            _ => IndexScreen.Results
        });
        var transfers = run.Keys.Where(item => item.Key == InputKey.D5).ToArray();
        Assert.Equal(2, transfers.Length);
        Assert.True(transfers[0].At < 8_100);
        Assert.True(transfers[1].At >= 10_000);
        Assert.Equal(InputKey.D5, run.Keys.First(item => item.At >= 10_000).Key);
        Assert.Equal([false], run.Votes);
        Assert.Equal(RunState.Stopped, run.Status.State);
    }

    [Fact]
    public async Task StaysBeforeTheTimerThenExtractsAtTheNextVoteAndFreezesRotationDuringBoth()
    {
        var run = await Execute(new() { Nokko = FixedRotation, Vote = IndexVoteMode.ExtractOnTimer, ExtractAfter = Timing.Fixed(20_000) },
            time => time switch
            {
                < 8_000 => IndexScreen.NoVote,
                < 14_000 => IndexScreen.Vote,
                < 55_000 => IndexScreen.NoVote,
                < 58_000 => IndexScreen.Vote,
                _ => IndexScreen.Results
            });
        Assert.Equal(new[] { false, true }, run.Votes);
        Assert.DoesNotContain(run.Keys, item => item.At is >= 8_125 and < 14_000 || item.At >= 55_125);
        var roar = run.Keys.Where(item => item.Key == InputKey.D3).ToArray();
        Assert.Equal(2, roar.Length);
        Assert.InRange(roar[1].At - roar[0].At, 46_100, 46_600);
        Assert.Equal("Cash In selected. Index results detected.", run.Status.Message);
        Assert.Equal(4, run.Events.Count(item => item.Event.Kind is InputKind.MouseButtonDown or InputKind.MouseButtonUp));
    }

    [Fact]
    public async Task StaysOnAVoteOpenedBeforeTheDeadlineEvenWhenTheClickNeedsARetry()
    {
        var run = await Execute(new() { Vote = IndexVoteMode.ExtractOnTimer, ExtractAfter = Timing.Fixed(2_250) },
            time => time switch
            {
                < 2_000 => IndexScreen.NoVote,
                < 3_000 => IndexScreen.Vote,
                < 5_000 => IndexScreen.NoVote,
                < 6_000 => IndexScreen.Vote,
                _ => IndexScreen.NoVote
            }, movePointerOnFirstAttempt: true);
        Assert.Equal([false, true], run.Votes);
        var clicks = run.Events.Where(item => item.Event.Kind == InputKind.MouseButtonDown).ToArray();
        Assert.Equal(2, clicks.Length);
        Assert.True(clicks[0].At > 2_250);
        Assert.Equal("Cash In clicked. Scenario stopped.", run.Status.Message);
    }

    [Fact]
    public async Task ManualVoteResumesAPartiallyCompletedMushroomBatch()
    {
        var run = await Execute(new() { Nokko = FixedRotation }, time => time switch
        {
            < 1_100 => IndexScreen.NoVote,
            < 1_250 => IndexScreen.Unavailable,
            < 3_000 => IndexScreen.Vote,
            < 9_000 => IndexScreen.NoVote,
            _ => IndexScreen.Results
        });
        Assert.Empty(run.Votes);
        Assert.Equal(new[] { InputKey.D2, InputKey.D2 }, run.Keys.Where(item => item.At < 3_000).Select(item => item.Key));
        Assert.Equal(InputKey.D1, run.Keys.First(item => item.At > 3_000).Key);
        Assert.Equal(4, run.Keys.Count(item => item.Key == InputKey.D1));
    }

    [Fact]
    public async Task HudFreezesTheRotationAtAVoteAndUsesTheActualOperatorAndCashInDeadlines()
    {
        var run = await Execute(new()
        {
            Vote = IndexVoteMode.ExtractOnTimer,
            ExtractAfter = Timing.Range(14_000, 14_000, 10),
            Nokko = FixedRotation with { OperatorInterval = Timing.Fixed(8_000) }
        }, time => time switch
        {
            < 1_100 => IndexScreen.NoVote,
            < 3_000 => IndexScreen.Vote,
            < 15_000 => IndexScreen.NoVote,
            < 17_000 => IndexScreen.Vote,
            _ => IndexScreen.Results
        }, captureHud: true);

        var before = run.Hud.Where(item => item.Hud.Phase == IndexHudPhase.StayInvested).ToArray();
        Assert.True(before.Length > 1);
        var frozen = before[0].Hud;
        Assert.Equal(2, frozen.CompletedCasts);
        Assert.True(frozen.PlannedCasts > frozen.CompletedCasts);
        Assert.Equal(14_000, frozen.CashInAfterMs);
        Assert.Equal(8_000, frozen.OperatorDueMs);
        Assert.NotNull(frozen.RotationFrozenAtMs);
        Assert.All(before, item => Assert.Equal(frozen, item.Hud));
        Assert.Contains(run.Hud, item => item.Hud.Phase == IndexHudPhase.Running &&
            item.Hud.Action == "Mushroom 1" && item.Hud.CompletedCasts >= 3 && item.Hud.RotationFrozenAtMs is null);

        var transfer = run.Keys.Where(item => item.Key == InputKey.D5).Take(2).ToArray();
        Assert.Equal(2, transfer.Length);
        Assert.Contains(run.Hud, item => item.Hud.InOperator && item.Hud.OperatorDueMs == transfer[1].At);
        Assert.Contains(run.Hud, item => item.Hud.Phase == IndexHudPhase.CashIn && item.Hud.RotationFrozenAtMs >= 15_000);
    }

    [Fact]
    public async Task VoteOnlyConfigStaysActiveAfterItsOneShotStepsFinish()
    {
        var run = await Execute(new() { Vote = IndexVoteMode.Stay }, time => time switch
        {
            < 1_000 => IndexScreen.NoVote,
            < 2_000 => IndexScreen.Vote,
            < 3_000 => IndexScreen.NoVote,
            _ => IndexScreen.Results
        }, [new() { Kind = ActionKind.KeyPress, Key = InputKey.F6 }]);
        Assert.Equal([false], run.Votes);
        Assert.Equal(InputKey.F6, Assert.Single(run.Keys).Key);
    }

    [Fact]
    public async Task DisablingTheRotationKeepsAutomaticRoundChoiceActive()
    {
        var run = await Execute(new() { Vote = IndexVoteMode.Stay, Nokko = FixedRotation with { Enabled = false } },
            time => time switch
            {
                < 1_000 => IndexScreen.NoVote,
                < 2_000 => IndexScreen.Vote,
                < 3_000 => IndexScreen.NoVote,
                _ => IndexScreen.Results
            });
        Assert.Equal(RunState.Stopped, run.Status.State);
        Assert.Equal([false], run.Votes);
        Assert.Empty(run.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CashInCanBeScheduledBeyondOneHour(bool random)
    {
        const int after = 4_500_000;
        var run = await Execute(new()
        {
            Vote = IndexVoteMode.ExtractOnTimer,
            ExtractAfter = random ? Timing.Range(after, after, 10) : Timing.Fixed(after)
        }, time => time switch
        {
            < 1_000 => IndexScreen.NoVote,
            < 2_000 => IndexScreen.Vote,
            < after => IndexScreen.NoVote,
            < after + 2_000 => IndexScreen.Vote,
            _ => IndexScreen.NoVote
        });
        Assert.Equal([false, true], run.Votes);
        Assert.Equal("Cash In clicked. Scenario stopped.", run.Status.Message);
    }

    [Fact]
    public async Task RespectsEveryCombinationOfEnabledBlocks()
    {
        for (int mask = 0; mask < 32; mask++)
        {
            var rotation = FixedRotation with
            {
                Mushroom2Enabled = (mask & 1) != 0,
                Mushroom1Enabled = (mask & 2) != 0,
                RoarEnabled = (mask & 4) != 0,
                FourthEnabled = (mask & 8) != 0,
                OperatorEnabled = (mask & 16) != 0
            };
            var run = await Execute(new() { Nokko = rotation },
                time => time < 110_000 ? IndexScreen.NoVote : IndexScreen.Results);
            Assert.Equal(RunState.Stopped, run.Status.State);
            var keys = run.Keys.Select(item => item.Key).Distinct().ToHashSet();
            Assert.Equal(rotation.Mushroom2Enabled, keys.Contains(InputKey.D2));
            Assert.Equal(rotation.Mushroom1Enabled, keys.Contains(InputKey.D1));
            Assert.Equal(rotation.RoarEnabled, keys.Contains(InputKey.D3));
            Assert.Equal(rotation.FourthEnabled, keys.Contains(InputKey.D4));
            Assert.Equal(rotation.OperatorEnabled, keys.Contains(InputKey.D5));
        }
    }


    [Fact]
    public async Task StartsImmediatelyWithoutAnIndexHud()
    {
        var run = await Execute(new() { Nokko = FixedRotation, Vote = IndexVoteMode.Stay },
            time => time < 5_000 ? IndexScreen.NoVote : IndexScreen.Results);
        Assert.Equal(RunState.Stopped, run.Status.State);
        Assert.Equal((InputKey.D2, 0L), run.Keys[0]);
        Assert.Empty(run.Votes);
    }

    [Fact]
    public async Task StartsOnTheVoteWithoutSendingSkillsAndWaitsForItToClear()
    {
        var run = await Execute(new() { Nokko = FixedRotation, Vote = IndexVoteMode.Stay }, time => time switch
        {
            < 2_000 => IndexScreen.Vote,
            < 2_250 => IndexScreen.NoVote,
            < 3_000 => IndexScreen.Vote,
            < 3_500 => IndexScreen.Unavailable,
            < 9_000 => IndexScreen.NoVote,
            _ => IndexScreen.Results
        });
        Assert.Equal([false], run.Votes);
        Assert.True(run.Keys[0].At >= 4_000);
    }

    [Fact]
    public async Task CashInStopsWithoutDependingOnRecognitionOfTheResultsScreen()
    {
        var run = await Execute(new() { Nokko = FixedRotation, Vote = IndexVoteMode.ExtractOnTimer, ExtractAfter = Timing.Fixed(0) },
            time => time < 2_000 ? IndexScreen.Vote : IndexScreen.NoVote);
        Assert.Equal([true], run.Votes);
        Assert.Empty(run.Keys);
        Assert.Equal("Cash In clicked. Scenario stopped.", run.Status.Message);
    }

    [Fact]
    public async Task FocusLossDuringVoteClickReleasesTheButton()
    {
        var run = await Execute(new() { Vote = IndexVoteMode.Stay }, time => time < 1_000 ? IndexScreen.NoVote : IndexScreen.Vote,
            loseFocusAfterClick: true);
        Assert.Equal([false], run.Votes);
        Assert.Equal(new[] { InputKind.MouseButtonDown, InputKind.MouseButtonUp }, run.Events.Select(item => item.Event.Kind));
        Assert.Equal(RunState.Stopped, run.Status.State);
    }

    [Fact]
    public async Task SavesAndReloadsThePresetAndItsModifiedSettings()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var profile = ScenarioProfile.Create("Nokko Index") with
            {
                Index = new() { Vote = IndexVoteMode.ExtractOnTimer, ExtractAfter = Timing.Range(60_000, 70_000, 10),
                    Nokko = new() { Roar = InputKey.Num3, OperatorStay = Timing.Fixed(300), Mushroom2Gap = Timing.Fixed(420) } }
            };
            await new ProfileService(folder).Save(profile);
            var (profiles, errors) = await new ProfileService(folder).LoadAll();
            Assert.Empty(errors);
            Assert.Equal(profile.Index, Assert.Single(profiles).Index);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<TestRun> Execute(IndexOptions options, Func<long, IndexScreen> screens,
        System.Collections.Immutable.ImmutableArray<ScenarioAction> actions = default, bool loseFocusAfterClick = false,
        bool movePointerOnFirstAttempt = false, bool captureHud = false, int waitOverrunMs = 0)
    {
        var clock = new TestClock(waitOverrunMs);
        var run = new TestRun(clock, screens, loseFocusAfterClick, movePointerOnFirstAttempt);
        var engine = new ScenarioExecutionEngine(new(new(new Random(7))), new(clock), new(run), run);
        run.ReadPlan = () => engine.Status.Plan;
        if (captureHud)
            run.ReadHud = () => engine.Status.IndexHud;
        var config = ScenarioProfile.Create("Test Index") with { Index = options, Actions = actions.IsDefault ? [] : actions };
        Assert.True(engine.Start(() => config, run, new(LatePolicy.Dispatch, 10)));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        run.Status = engine.Status;
        return run;
    }

    private sealed class TestClock(int waitOverrunMs = 0) : IDeadlineClock
    {
        public long Frequency => 1_000;
        public long Timestamp { get; private set; }
        public void WaitUntil(long deadline, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Timestamp = Math.Max(Timestamp, deadline) + waitOverrunMs;
        }
    }

    private sealed class TestRun(TestClock clock, Func<long, IndexScreen> screens, bool loseFocusAfterClick, bool movePointerOnFirstAttempt)
        : IIndexTarget, IWindowTarget, IInputBackend, IRunLog
    {
        private long? _clickStarted;
        private bool? _pendingChoice;
        private bool _firstPoint = true;
        public List<bool> Votes { get; } = [];
        public List<string> Log { get; } = [];
        public List<(InputEvent Event, long At)> Events { get; } = [];
        public (InputKey Key, long At)[] Keys => Events.Where(item => item.Event.Kind == InputKind.KeyDown)
            .Select(item => ((InputKey)item.Event.Value, item.At)).ToArray();
        public RunStatus Status { get; set; } = new(RunState.Idle);
        public ExecutionPlan? FirstPlan { get; private set; }
        public Func<ExecutionPlan?> ReadPlan { private get; set; } = () => null;
        public Func<IndexRunHud?>? ReadHud { private get; set; }
        public List<(long At, IndexRunHud Hud)> Hud { get; } = [];
        public bool TryArm() => true;
        public bool HasFocus() => !loseFocusAfterClick || _clickStarted is not long started || clock.Timestamp < started + 30;
        public void VerifyPointer(InputEvent input) { }
        public IndexScreen ReadIndexScreen()
        {
            if (ReadHud?.Invoke() is { } hud)
                Hud.Add((clock.Timestamp, hud));
            return screens(clock.Timestamp);
        }
        public bool TryPointAtIndexChoice(bool extract)
        {
            Assert.Equal(IndexScreen.Vote, screens(clock.Timestamp));
            _pendingChoice = extract;
            return true;
        }
        public bool IsPointingAtIndexChoice()
        {
            bool valid = !_firstPoint || !movePointerOnFirstAttempt;
            _firstPoint = false;
            return valid;
        }
        public bool IsPressed(InputEvent input) => false;
        public void Send(InputEvent input)
        {
            FirstPlan ??= ReadPlan();
            if (input.Kind == InputKind.MouseButtonDown)
            {
                _clickStarted = clock.Timestamp;
                if (_pendingChoice is bool choice)
                    Votes.Add(choice);
                _pendingChoice = null;
            }
            Events.Add((input, clock.Timestamp));
        }
        public void Write(string message, bool detailed = false) => Log.Add(message);
    }
}
