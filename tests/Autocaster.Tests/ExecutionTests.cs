using Autocaster.Core;
using static Autocaster.Tests.CompilerTests;

namespace Autocaster.Tests;

public sealed class ExecutionTests
{
    [Fact]
    public async Task LarvaReleasesTheNextInputAtTheFreshCountdownWithoutMakingLaterEventsLate()
    {
        var clock = new FakeClock();
        List<(string Name, long Time)> trace = [];
        var input = new FakeInput { OnSend = item => trace.Add((item.Kind.ToString(), clock.Now)) };
        var gate = new ScenarioAction { Kind = ActionKind.LarvaDetect };
        ScenarioExecutionEngine? engine = null;
        var target = new Target
        {
            ReadLarva = () =>
            {
                if (clock.Now == 36)
                    Assert.Equal(gate.Id, engine!.Status.WaitingForHud?.ActionId);
                return clock.Now < 36 || clock.Now >= 100 ? LarvaHudState.Countdown : LarvaHudState.Idle;
            }
        };
        var profile = Profile(
            new() { Kind = ActionKind.KeyPress, Key = InputKey.D2, Hold = Timing.Fixed(20) },
            new() { Kind = ActionKind.StartMacroTimer, MacroTimerDurationMs = 1000 }, gate,
            new() { Kind = ActionKind.StartMacroTimer, MacroTimerDurationMs = 2000 },
            new() { Kind = ActionKind.KeyPress, Key = InputKey.D2, Hold = Timing.Fixed(15) },
            new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(60) },
            new() { Kind = ActionKind.KeyPress, Key = InputKey.D1, Hold = Timing.Fixed(10) })
            with { Macro = new() };
        engine = new(new(new()), new(clock), new(input), new NoLog());
        Assert.True(engine.Start(() => profile, target, new(LatePolicy.Stop, 1),
            duration => trace.Add(($"Timer {duration}", clock.Now))));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(RunState.Stopped, engine.Status.State);
        Assert.Equal<(string, long)>([("KeyDown", 0), ("KeyUp", 20), ("Timer 1000", 20),
            ("Timer 2000", 108), ("KeyDown", 108), ("KeyUp", 123), ("KeyDown", 183), ("KeyUp", 193)], trace);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LarvaNeverContinuesOnAnOldCountdownOrAfterFocusLoss(bool loseFocus)
    {
        var clock = new FakeClock();
        var target = new Target { ReadLarva = () => LarvaHudState.Countdown };
        if (loseFocus)
            clock.Waited = () => target.Focused = clock.Now < 45;
        var input = new FakeInput();
        var profile = Profile(new() { Kind = ActionKind.KeyPress, Key = InputKey.D2, Hold = Timing.Fixed(20) },
            new() { Kind = ActionKind.LarvaDetect, LarvaTimeoutMs = 100 },
            new() { Kind = ActionKind.KeyPress, Key = InputKey.D2 });
        var engine = new ScenarioExecutionEngine(new(new()), new(clock), new(input), new NoLog());
        Assert.True(engine.Start(() => profile, target, new()));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, input.Events.Count);
        Assert.Contains(loseFocus ? "lost focus" : "timeout", engine.Status.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RathuumGateUsesTheExactForecastOrTimesOutWithoutSendingNextInput(bool ready)
    {
        var clock = new FakeClock();
        List<(InputKind Kind, long Time)> trace = [];
        var input = new FakeInput { OnSend = item => trace.Add((item.Kind, clock.Now)) };
        var monitor = new RathuumMonitor(clock, ready ? 275 : null);
        var target = new Target { Rathuum = monitor };
        var gate = new ScenarioAction { Kind = ActionKind.RathuumKillFeed,
            Rathuum = new() { TimeoutMs = 300 } };
        var profile = Profile(
            new() { Kind = ActionKind.KeyPress, Hold = Timing.Fixed(20) }, gate,
            new() { Kind = ActionKind.KeyPress, Hold = Timing.Fixed(15) });
        var engine = new ScenarioExecutionEngine(new(new()), new(clock), new(input), new NoLog());
        var observedDeadlines = new List<long?>();
        clock.Waited = () =>
        {
            if (engine.Status.WaitingForHud?.ActionId == gate.Id)
                observedDeadlines.Add(engine.Status.RathuumLaunchAt);
        };
        Assert.True(engine.Start(() => profile, target, new(LatePolicy.Stop, 1)));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotEmpty(observedDeadlines);
        Assert.All(observedDeadlines, deadline => Assert.Equal(ready ? 275L : null, deadline));
        Assert.Null(engine.Status.RathuumLaunchAt);
        Assert.Equal(0, monitor.StartedAt);
        Assert.True(monitor.Disposed);
        Assert.Equal(ready ? RunState.Stopped : RunState.Error, engine.Status.State);
        if (ready)
            Assert.Equal<(InputKind, long)>([(InputKind.KeyDown, 0), (InputKind.KeyUp, 20),
                (InputKind.KeyDown, 275), (InputKind.KeyUp, 290)], trace);
        else
        {
            Assert.Equal(2, trace.Count);
            Assert.Contains("timed out", engine.Status.Message);
        }

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MacroStartsKillInputsImmediatelyWhileRathuumWaitsAtItsOwnStep(bool hasBaseline)
    {
        var clock = new FakeClock();
        List<(InputKind Kind, long Time)> trace = [];
        var input = new FakeInput { OnSend = item => trace.Add((item.Kind, clock.Now)) };
        var monitor = new RathuumMonitor(clock, 140, hasBaseline ? 80 : long.MaxValue);
        var target = new Target { Rathuum = monitor };
        var profile = Profile(
            new() { Kind = ActionKind.KeyPress, Key = InputKey.Space, Hold = Timing.Fixed(20) },
            new() { Kind = ActionKind.KeyPress, Key = InputKey.Backslash, Hold = Timing.Fixed(20), RunWithPrevious = true },
            new() { Kind = ActionKind.RathuumKillFeed, Rathuum = new() { TimeoutMs = 150 } },
            new() { Kind = ActionKind.KeyPress, Hold = Timing.Fixed(15) }) with { Macro = new() };
        var engine = new ScenarioExecutionEngine(new(new()), new(clock), new(input), new NoLog());
        Assert.True(engine.Start(() => profile, target, new(LatePolicy.Stop, 1)));
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        if (hasBaseline)
        {
            Assert.Equal(RunState.Stopped, engine.Status.State);
            Assert.Equal<(InputKind, long)>([(InputKind.KeyDown, 0), (InputKind.KeyUp, 20),
                (InputKind.KeyDown, 20), (InputKind.KeyUp, 40), (InputKind.KeyDown, 140), (InputKind.KeyUp, 155)], trace);
        }
        else
        {
            Assert.Equal<(InputKind, long)>([(InputKind.KeyDown, 0), (InputKind.KeyUp, 20),
                (InputKind.KeyDown, 20), (InputKind.KeyUp, 40)], trace);
            Assert.Contains("timed out", engine.Status.Message);
        }
    }

    [Fact]
    public void MacroTimerStartsAfterReleaseAndAfterADelayBeforeTheNextInput()
    {
        var clock = new FakeClock();
        var scheduler = new ExecutionScheduler(clock);
        var profile = Profile(
            new() { Kind = ActionKind.KeyPress, Hold = Timing.Fixed(20) },
            new() { Kind = ActionKind.StartMacroTimer, MacroTimerDurationMs = 1500 },
            new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(500) },
            new() { Kind = ActionKind.StartMacroTimer, MacroTimerDurationMs = 4200 },
            new() { Kind = ActionKind.KeyPress, Key = InputKey.B, Hold = Timing.Fixed(15) })
            with { Macro = new MacroOptions { Timer = new() } };
        var plan = new ScenarioCompiler(new()).Compile(profile);
        List<(string, long)> trace = [];
        scheduler.Execute(plan, 0, new(), () => true, input => trace.Add((input.Kind.ToString(), clock.Now)),
            _ => { }, default, duration => trace.Add(($"timer {duration}", clock.Now)));
        Assert.Equal<(string, long)>([("KeyDown", 0), ("KeyUp", 20), ("timer 1500", 20), ("timer 4200", 520),
            ("KeyDown", 520), ("KeyUp", 535)], trace);
    }

    [Fact]
    public void InterruptedMacroDoesNotReachTheTimer()
    {
        var clock = new FakeClock();
        var scheduler = new ExecutionScheduler(clock);
        var profile = Profile(
            new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(80) },
            new() { Kind = ActionKind.StartMacroTimer })
            with { Macro = new MacroOptions() };
        var plan = new ScenarioCompiler(new()).Compile(profile);
        int starts = 0;
        Assert.Throws<TargetLostException>(() => scheduler.Execute(plan, 0, new(), () => clock.Now < 70,
            _ => { }, _ => { }, default, () => starts++));
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData(false, 300)]
    [InlineData(true, 600)]
    public void AutomaticStepActionsWaitForTheEntireMouseRoute(bool reverse, int routeEnd)
    {
        var clock = new FakeClock();
        var profile = Profile(
            new() { Kind = ActionKind.MouseMovement, MousePath = [new(8, 2, 1), new(16, 4, 3), new(24, 0, 0)],
                ReverseMousePath = reverse, VaryMousePath = false, MouseDurationMs = 300 },
            new() { Kind = ActionKind.StartMacroTimer, RunWithPrevious = true },
            new() { Kind = ActionKind.KeyPress, Hold = Timing.Fixed(20), RunWithPrevious = true },
            new() { Kind = ActionKind.KeyPress, Key = InputKey.B })
            with { Macro = new MacroOptions(Step: true) };
        var steps = new MacroSteps();
        var compiler = new ScenarioCompiler(new());
        var plan = compiler.Compile(steps.Plan(profile));
        var trace = new List<(string Kind, long Ms)>();
        new ExecutionScheduler(clock).Execute(plan, 0, new(), () => true,
            input => trace.Add((input.Kind.ToString(), clock.Now)), _ => { }, default,
            () => trace.Add(("Timer", clock.Now)));

        Assert.Equal(reverse ? 4 : 2, trace.Count(entry => entry.Kind == "MouseMove"));
        Assert.All(trace.Take(trace.Count - 3), entry => Assert.Equal("MouseMove", entry.Kind));
        if (reverse)
            Assert.Equal(("MouseMove", 600L), trace[^4]);
        Assert.Equal<(string, long)>([("Timer", routeEnd), ("KeyDown", routeEnd), ("KeyUp", routeEnd + 20)], trace.TakeLast(3));
        steps.Advance(profile);
        Assert.Equal(profile.Actions[3].Id, Assert.Single(steps.Plan(profile).Actions).Id);
    }


    [Fact]
    public void DeadlinesStayAbsoluteWhenDispatchTakesTime()
    {
        var clock = new FakeClock();
        var scheduler = new ExecutionScheduler(clock);
        var profile = Profile(
            new() { Kind = ActionKind.KeyPress, Hold = Timing.Fixed(30) },
            new() { Kind = ActionKind.KeyPress, Before = Timing.Fixed(20), Hold = Timing.Fixed(30) });
        var plan = new ScenarioCompiler(new()).Compile(profile);
        List<double> actual = [];
        scheduler.Execute(plan, 0, new(), () => true, _ => clock.Now += 35, s => actual.Add(s.ActualMs), default);
        Assert.Equal([0d, 35d, 70d, 105d], actual);
        Assert.Equal(140, clock.Now);

        clock.Now = 0;
        actual.Clear();
        scheduler.Execute(plan, 0, new(), () => true, _ => clock.Now += 7, s => actual.Add(s.ActualMs), default);
        Assert.Equal([0d, 30d, 50d, 80d], actual);
    }

    [Fact]
    public void LatePolicySkipsWheelButDoesNotSkipKeyRelease()
    {
        var clock = new FakeClock();
        var scheduler = new ExecutionScheduler(clock);
        var plan = new ScenarioCompiler(new()).Compile(Profile(
            new() { Kind = ActionKind.KeyDown }, new() { Kind = ActionKind.MouseWheel }, new() { Kind = ActionKind.KeyUp }));
        List<EventSample> samples = [];
        scheduler.Execute(plan, 0, new(LatePolicy.SkipWheel, 5), () => true,
            _ => clock.Now += 10, samples.Add, default);
        Assert.Equal([SendResult.Sent, SendResult.Skipped, SendResult.Sent], samples.Select(sample => sample.Result));
        Assert.Equal(InputKind.KeyUp, samples[^1].Event.Type);
        Assert.Throws<InvalidOperationException>(() => scheduler.Execute(plan, 0, new(LatePolicy.Stop, 5),
            () => true, _ => throw new Exception("Must not dispatch"), _ => { }, default));
    }

    [Fact]
    public void CancellationAndFocusAreCheckedDuringLongWait()
    {
        var clock = new FakeClock();
        var scheduler = new ExecutionScheduler(clock);
        var plan = new ScenarioCompiler(new()).Compile(Profile(new ScenarioAction { Kind = ActionKind.Wait, Delay = Timing.Fixed(1000) }));
        using var cancel = new CancellationTokenSource();
        clock.Waited = cancel.Cancel;
        Assert.Throws<OperationCanceledException>(() => scheduler.Execute(plan, 0, new(), () => true, _ => { }, _ => { }, cancel.Token));
        Assert.Equal(25, clock.Now);
        clock.Waited = null;
        Assert.Throws<TargetLostException>(() => scheduler.Execute(plan, 0, new(), () => clock.Now < 40, _ => { }, _ => { }, default));
        Assert.Equal(50, clock.Now);
    }

    [Fact]
    public void CleanupOnlyReleasesOwnedInputsAndRetriesFailedKeyUp()
    {
        var input = new FakeInput();
        var ownership = new InputOwnership(input);
        var a = new InputEvent(InputKind.KeyDown, (int)InputKey.A);
        var b = new InputEvent(InputKind.KeyDown, (int)InputKey.B);
        input.External = b;
        Assert.Throws<InvalidOperationException>(() => ownership.Dispatch(b));
        input.Fail = a;
        Assert.Throws<InvalidOperationException>(() => ownership.Dispatch(a));
        Assert.Null(ownership.ReleaseAll());
        input.Fail = null;
        ownership.Dispatch(a);
        input.Fail = a.Release;
        Assert.Throws<InvalidOperationException>(() => ownership.Dispatch(a.Release));
        Assert.NotNull(ownership.ReleaseAll());
        input.Fail = null;
        Assert.Null(ownership.ReleaseAll());
        Assert.Null(ownership.ReleaseAll());
        Assert.Equal([a, a.Release], input.Events);
    }

    [Fact]
    public async Task StopCancelsInFlightPlanAndSerializesConcurrentStarts()
    {
        using var clock = new BlockingClock();
        var input = new FakeInput();
        var engine = new ScenarioExecutionEngine(new(new()), new(clock), new(input), new NoLog());
        var profile = Profile(new() { Kind = ActionKind.KeyDown }, new() { Kind = ActionKind.Wait });
        var target = new Target();

        bool[] started = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => engine.Start(() => profile, target, new()))));
        Assert.Single(started, value => value);
        try
        {
            await clock.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await engine.Stop().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(RunState.Stopped, engine.Status.State);
        Assert.Equal([InputKind.KeyDown, InputKind.KeyUp], input.Events.Select(item => item.Kind));
    }

    [Fact]
    public async Task ReadsASeparateSnapshotForEachCycle()
    {
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new FakeClock();
        var input = new FakeInput();
        var log = new NoLog();
        var engine = new ScenarioExecutionEngine(new(new()), new(clock), new(input), log);
        var original = Profile(new ScenarioAction { Kind = ActionKind.KeyPress, Key = InputKey.D1 }) with { Mode = CycleMode.Count, CycleCount = 2 };
        var updated = original with { Actions = [original.Actions[0] with { Key = InputKey.D2 }] };
        int reads = 0;
        Assert.True(engine.Start(() => Interlocked.Increment(ref reads) == 1 ? original : updated, new Target(), new()));
        while (engine.Status.State is not (RunState.Stopped or RunState.Error))
            await Task.Delay(10, done.Token);
        Assert.Equal(RunState.Stopped, engine.Status.State);
        Assert.Equal(["D1", "D1", "D2", "D2"], input.Events.Select(item => item.Parameter));
    }

    [Fact]
    public async Task FocusLossReleasesHeldKeyBeforeStopping()
    {
        var target = new Target();
        var clock = new FakeClock { Waited = () => target.Focused = false };
        var input = new FakeInput();
        var engine = new ScenarioExecutionEngine(new(new()), new(clock), new(input), new NoLog());
        var profile = Profile(new() { Kind = ActionKind.KeyDown }, new() { Kind = ActionKind.Wait });

        Assert.True(engine.Start(() => profile, target, new()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.Status.State is not (RunState.Stopped or RunState.Error))
            await Task.Delay(10, timeout.Token);
        Assert.Equal(RunState.Stopped, engine.Status.State);
        Assert.Contains("focus", engine.Status.Message);
        Assert.Equal([InputKind.KeyDown, InputKind.KeyUp], input.Events.Select(item => item.Kind));
    }

    [Fact]
    public async Task LosingFocusBetweenCyclesDoesNotArmAgain()
    {
        var target = new Target();
        var input = new FakeInput();
        var log = new NoLog(message =>
        {
            if (message.StartsWith("Cycle 1 complete", StringComparison.Ordinal))
                target.Focused = false;
        });
        var engine = new ScenarioExecutionEngine(new(new()), new(new FakeClock()), new(input), log);
        var profile = Profile(new ScenarioAction { Kind = ActionKind.KeyPress }) with { Mode = CycleMode.Count, CycleCount = 2 };

        Assert.True(engine.Start(() => profile, target, new()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.Status.State is not (RunState.Stopped or RunState.Error))
            await Task.Delay(10, timeout.Token);
        Assert.Equal(RunState.Stopped, engine.Status.State);
        Assert.Contains("focus", engine.Status.Message);
        Assert.Equal([InputKind.KeyDown, InputKind.KeyUp], input.Events.Select(item => item.Kind));
    }

    [Fact]
    public void TimedHoldReleasesDuringAnotherDelayWithoutMovingTheNextAction()
    {
        var clock = new FakeClock();
        var plan = new ScenarioCompiler(new()).Compile(Profile(
            new() { Kind = ActionKind.KeyHold, Key = InputKey.A, Hold = Timing.Fixed(100) },
            new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(40) },
            new() { Kind = ActionKind.KeyPress, Key = InputKey.B, Hold = Timing.Fixed(10) },
            new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(100) }));
        var events = new List<(long Time, string Key, InputKind Kind)>();
        new ExecutionScheduler(clock).Execute(plan, 0, new(), () => true,
            input => events.Add((clock.Now, input.Parameter, input.Kind)), _ => { }, default);

        Assert.Equal([(0L, "A", InputKind.KeyDown), (40L, "B", InputKind.KeyDown),
            (50L, "B", InputKind.KeyUp), (100L, "A", InputKind.KeyUp)], events);
        Assert.Equal(150, plan.DurationMs);
    }

    [Fact]
    public async Task TimedHoldCarriesAcrossCycleBoundariesAndReleasesAfterTheFinalCycle()
    {
        var clock = new FakeClock();
        var input = new FakeInput();
        var events = new List<(long Time, string Key, InputKind Kind)>();
        input.OnSend = value => events.Add((clock.Now, value.Parameter, value.Kind));
        var engine = new ScenarioExecutionEngine(new(new()), new(clock), new(input), new NoLog());
        var profile = Profile(
            new() { Kind = ActionKind.KeyHold, Key = InputKey.A, Hold = Timing.Fixed(100) },
            new() { Kind = ActionKind.Wait, Delay = Timing.Fixed(40) },
            new() { Kind = ActionKind.KeyPress, Key = InputKey.B, Hold = Timing.Fixed(0) },
            new() { Kind = ActionKind.Loop, LoopCount = 2 },
            new() { Kind = ActionKind.KeyPress, Key = InputKey.C, Hold = Timing.Fixed(0) })
            with { BetweenCycles = Timing.Fixed(0) };

        Assert.True(engine.Start(() => profile, new Target(), new()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.Status.State is not (RunState.Stopped or RunState.Error))
            await Task.Delay(10, timeout.Token);
        Assert.Equal(RunState.Stopped, engine.Status.State);
        Assert.Equal([(0L, "A", InputKind.KeyDown), (40L, "B", InputKind.KeyDown), (40L, "B", InputKind.KeyUp),
            (80L, "B", InputKind.KeyDown), (80L, "B", InputKind.KeyUp), (100L, "A", InputKind.KeyUp)], events);
    }

    [Fact]
    public void APressDuringATimedHoldDoesNotReleaseTheSameKeyOrStopTheQueue()
    {
        var clock = new FakeClock();
        var plan = new ScenarioCompiler(new()).Compile(Profile(
            new() { Kind = ActionKind.KeyHold, Key = InputKey.A, Hold = Timing.Fixed(50) },
            new() { Kind = ActionKind.KeyPress, Key = InputKey.A, Hold = Timing.Fixed(80) },
            new() { Kind = ActionKind.KeyPress, Key = InputKey.B, Hold = Timing.Fixed(10) }));
        var events = new List<(long Time, string Key, InputKind Kind)>();
        new ExecutionScheduler(clock).Execute(plan, 0, new(), () => true,
            input => events.Add((clock.Now, input.Parameter, input.Kind)), _ => { }, default);

        Assert.Equal([(0L, "A", InputKind.KeyDown), (50L, "A", InputKind.KeyUp),
            (80L, "B", InputKind.KeyDown), (90L, "B", InputKind.KeyUp)], events);
    }

    [Fact]
    public void ConcurrentRunsReleaseSharedInputOnlyAfterTheLastOwner()
    {
        var backend = new FakeInput();
        var shared = new SharedInputBackend(backend);
        var first = new InputOwnership(shared);
        var second = new InputOwnership(shared);
        var key = new InputEvent(InputKind.KeyDown, (int)InputKey.A);
        first.Dispatch(key);
        second.Dispatch(key);
        Assert.Null(first.ReleaseAll());
        Assert.Equal([key], backend.Events);
        Assert.Null(second.ReleaseAll());
        Assert.Equal([key, key.Release], backend.Events);
    }

    private sealed class FakeClock : IDeadlineClock
    {
        public long Now { get; set; }
        public Action? Waited { get; set; }
        public long Timestamp => Now;
        public long Frequency => 1000;
        public void WaitUntil(long deadline, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Now = deadline;
            Waited?.Invoke();
        }
    }

    private sealed class BlockingClock : IDeadlineClock, IDisposable
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Timestamp => 0;
        public long Frequency => 1000;
        public void WaitUntil(long deadline, CancellationToken cancellation)
        {
            Waiting.TrySetResult();
            cancellation.WaitHandle.WaitOne();
            cancellation.ThrowIfCancellationRequested();
        }
        public void Dispose() => Waiting.TrySetCanceled();
    }

    private sealed class FakeInput : IInputBackend
    {
        public List<InputEvent> Events { get; } = [];
        public InputEvent? Fail { get; set; }
        public InputEvent? External { get; set; }
        public Action<InputEvent>? OnSend { get; set; }
        public bool IsPressed(InputEvent input) => External == input;
        public void Send(InputEvent input)
        {
            if (input == Fail)
                throw new InvalidOperationException("Fake failure");
            Events.Add(input);
            OnSend?.Invoke(input);
        }
    }

    private sealed class RathuumMonitor(FakeClock clock, long? launch, long baselineAt = 0) : IRathuumMonitor
    {
        public long? StartedAt { get; private set; }
        public bool Disposed { get; private set; }
        public void Start() => StartedAt = clock.Now;
        public RathuumGateReading Read() => clock.Now < baselineAt
            ? new(new(0, null, 5, 0, RathuumWaveStatus.AwaitingBaseline, null))
            : new(new(1, 0, 5, 5, RathuumWaveStatus.Gathering, null), launch);
        public bool TryConsume(long expectedLaunch) => launch == expectedLaunch && expectedLaunch == clock.Now;
        public void Dispose() => Disposed = true;
    }

    private sealed class Target : IWindowTarget, ILarvaTarget, IRathuumTarget
    {
        public RathuumMonitor? Rathuum { get; init; }
        public IRathuumMonitor StartRathuumMonitor(RathuumGateOptions options)
        {
            Rathuum!.Start();
            return Rathuum;
        }

        public Func<LarvaHudState>? ReadLarva { get; init; }
        public LarvaHudState ReadLarvaHud() => ReadLarva?.Invoke() ?? LarvaHudState.Idle;
        public bool TryArm() => true;
        public bool Focused { get; set; } = true;
        public bool HasFocus() => Focused;
        public void VerifyPointer(InputEvent input) { }
    }

    private sealed class NoLog(Action<string>? observe = null) : IRunLog
    {
        public void Write(string message, bool detailed = false) => observe?.Invoke(message);
    }
}
