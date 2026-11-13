using System.Collections.Immutable;

namespace Autocaster.Core;

public enum IndexVoteMode { Manual, Stay, ExtractOnTimer }
public enum IndexScreen { NoVote, Vote, Results, Unavailable }

public sealed record IndexOptions
{
    public const int MaxExtractAfterMs = 24 * 60 * 60 * 1000;

    public IndexVoteMode Vote { get; init; }
    public Timing ExtractAfter { get; init; } = Timing.Fixed(600_000);
    public NokkoOptions? Nokko { get; init; }
    public bool WatchesRound => Vote != IndexVoteMode.Manual || Nokko is not null;

    internal void Validate()
    {
        if (!Enum.IsDefined(Vote))
            throw new ScenarioException("Select a valid Index round choice.");
        TimingEngine.Validate(ExtractAfter, MaxExtractAfterMs);
        Nokko?.Validate();
    }
}

public sealed record NokkoOptions
{
    public bool Enabled { get; init; } = true;
    public InputKey Mushroom2 { get; init; } = InputKey.D2;
    public InputKey Mushroom1 { get; init; } = InputKey.D1;
    public InputKey Roar { get; init; } = InputKey.D3;
    public InputKey Fourth { get; init; } = InputKey.D4;
    public InputKey Operator { get; init; } = InputKey.D5;
    public bool Mushroom2Enabled { get; init; } = true;
    public bool Mushroom1Enabled { get; init; } = true;
    public bool RoarEnabled { get; init; } = true;
    public bool FourthEnabled { get; init; } = true;
    public bool OperatorEnabled { get; init; } = true;
    public int Mushroom2Count { get; init; } = 2;
    public int Mushroom1Count { get; init; } = 4;
    public Timing MushroomLifetime { get; init; } = Timing.Fixed(31_880);
    public Timing MushroomLead { get; init; } = Timing.Range(2_500, 3_500, 10);
    public Timing CastGap { get; init; } = Timing.Range(650, 850, 10);
    public Timing? Mushroom2Gap { get; init; }
    public Timing? Mushroom1Gap { get; init; }
    public Timing RoarLifetime { get; init; } = Timing.Fixed(38_250);
    public Timing RoarExtra { get; init; } = Timing.Range(1_500, 2_500, 10);
    public Timing FourthInterval { get; init; } = Timing.Range(750, 850, 10);
    public Timing OperatorInterval { get; init; } = Timing.Fixed(100_000);
    public Timing OperatorStay { get; init; } = Timing.Fixed(300);

    public Timing MushroomCastGap(bool second) => (second ? Mushroom2Gap : Mushroom1Gap) ?? CastGap;

    internal int ActiveMushroom2Count => Mushroom2Enabled ? Mushroom2Count : 0;
    internal int ActiveMushroom1Count => Mushroom1Enabled ? Mushroom1Count : 0;
    internal int ActiveMushroomCount => ActiveMushroom2Count + ActiveMushroom1Count;
    internal static int Maximum(Timing value) => value.Random ? value.MaxMs : value.FixedMs;
    private static int Minimum(Timing value) => value.Random ? value.MinMs : value.FixedMs;

    internal void Validate()
    {
        if (new[] { Mushroom2, Mushroom1, Roar, Fourth, Operator }.Any(key =>
            !InputKeys.IsKeyboard(key)))
            throw new ScenarioException("Assign keyboard keys to all Nokko abilities.");
        if (Mushroom2Count is < 1 or > 12 || Mushroom1Count is < 1 or > 12)
            throw new ScenarioException("Each mushroom count must be between 1 and 12.");
        Check(MushroomLifetime, "Mushroom lifetime", 1_000);
        Check(MushroomLead, "Mushroom refresh lead", 0);
        Check(CastGap, "Cast gap", 100);
        if (Mushroom2Gap is { } brightbonnetGap)
            Check(brightbonnetGap, "Brightbonnet cast gap", 100);
        if (Mushroom1Gap is { } stinkbrainGap)
            Check(stinkbrainGap, "Stinkbrain cast gap", 100);
        Check(RoarLifetime, "Roar lifetime", 1_000);
        Check(RoarExtra, "Extra time after Roar", 0);
        Check(FourthInterval, "Fourth ability interval", 150);
        Check(OperatorInterval, "Operator interval", 1_000);
        Check(OperatorStay, "Time in Operator", 50);
        long batchTime = ActiveMushroom2Count * (long)Maximum(MushroomCastGap(second: true)) +
            ActiveMushroom1Count * (long)Maximum(MushroomCastGap(second: false));
        if (ActiveMushroomCount > 0 && Minimum(MushroomLifetime) - Maximum(MushroomLead) <= batchTime + Maximum(CastGap))
            throw new ScenarioException("Leave enough mushroom lifetime to cast the whole batch before its next refresh.");
        if (OperatorEnabled && Minimum(OperatorInterval) <= Maximum(OperatorStay) + Maximum(CastGap))
            throw new ScenarioException("The Operator interval must exceed the time in Operator plus the cast gap.");

        if (RoarEnabled && Minimum(RoarLifetime) + Minimum(RoarExtra) <= Maximum(CastGap))
            throw new ScenarioException("The Roar timer must exceed the cast gap.");
        static void Check(Timing value, string label, int minimum)
        {
            TimingEngine.Validate(value);
            if (Minimum(value) < minimum)
                throw new ScenarioException($"{label} must be at least {minimum / 1000d:0.###} s.");
        }
    }
}

public interface IIndexTarget
{
    IndexScreen ReadIndexScreen();
    bool TryPointAtIndexChoice(bool extract);
    bool IsPointingAtIndexChoice();
}

internal readonly record struct NokkoProgress(long Mushrooms, long Roar, long Fourth, long Operator,
    long Ready, int MushroomsLeft = 0, bool Setup = true, long? Return = null, int ReturnGapMs = 0);

internal sealed record NokkoPlan(ExecutionPlan Plan, NokkoProgress Initial,
    IReadOnlyDictionary<Guid, NokkoProgress> AfterPress);

internal static class NokkoPlanner
{
    // The game can extend a round when points are banked. This window is planned
    // in full, then extended if no vote has appeared by the end of the window.
    internal const int HorizonMs = 1_800_000;
    private static readonly Timing KeyHold = Timing.Range(15, 30, 1);

    internal static NokkoPlan Build(ScenarioProfile profile, NokkoOptions options, TimingEngine timing,
        ScenarioCompiler compiler, long start, NokkoProgress? previous, CancellationToken cancellation)
    {
        options.Validate();
        if (!options.Enabled)
            options = options with { Mushroom2Enabled = false, Mushroom1Enabled = false, RoarEnabled = false,
                FourthEnabled = false, OperatorEnabled = false };
        var actions = ImmutableArray.CreateBuilder<ScenarioAction>();
        var afterPress = new Dictionary<Guid, NokkoProgress>();
        var transferTaps = new Dictionary<Guid, (int Hold, int ReleaseGap)>();
        var initial = previous ?? new NokkoProgress(
            options.ActiveMushroomCount > 0 ? start : long.MaxValue,
            options.RoarEnabled ? start : long.MaxValue,
            options.FourthEnabled ? start : long.MaxValue,
            options.OperatorEnabled ? start + timing.Resolve(options.OperatorInterval) : long.MaxValue,
            start, Setup: options.ActiveMushroomCount > 0 || options.RoarEnabled || options.FourthEnabled);
        var state = initial;
        long end = start + HorizonMs;
        long offset = start;
        int castClearance = NokkoOptions.Maximum(options.CastGap);
        int transferClearance = NokkoOptions.Maximum(options.OperatorStay) + castClearance;

        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            long due = state.Return ?? (state.MushroomsLeft > 0 ? state.Ready :
                Math.Min(Math.Min(state.Mushrooms, state.Roar), Math.Min(state.Fourth, state.Operator)));
            long cursor = Math.Max(offset, Math.Max(state.Ready, due));
            if (!state.Setup && state.Return is null)
            {
                long priorityDue = Math.Min(state.Mushrooms, Math.Min(state.Roar, state.Operator));
                if (state.MushroomsLeft == 0 && state.Fourth <= cursor && priorityDue > cursor && priorityDue - cursor < castClearance)
                    cursor = priorityDue;

                // Leave Roar's independent deadline clear of a fresh cast or a trip into Operator.
                int clearance = state.Operator <= cursor ? transferClearance : castClearance;
                if (state.Roar > cursor && state.Roar - cursor < clearance)
                    cursor = state.Roar;
            }
            if (cursor >= end)
                break;

            InputKey key;
            string name;
            int hold = timing.Resolve(KeyHold);
            int? releaseGap = null;
            if (state.Return is not null)
            {
                key = options.Operator;
                name = "Return to Nokko";
                hold = 80;
                releaseGap = state.ReturnGapMs;
                state = state with { Return = null, Ready = cursor + timing.Resolve(options.CastGap) };
            }
            else if ((!state.Setup || (state.MushroomsLeft == 0 && state.Mushrooms > cursor)) && state.Roar <= cursor)
            {
                key = options.Roar;
                name = "Roar";
                state = state with { Roar = cursor + timing.Resolve(options.RoarLifetime) + timing.Resolve(options.RoarExtra),
                    Ready = cursor + timing.Resolve(options.CastGap), Setup = state.Setup && options.FourthEnabled };
            }
            else if (!state.Setup && state.Operator <= cursor)
            {
                key = options.Operator;
                name = "Enter Operator";
                int stay = timing.Resolve(options.OperatorStay);
                hold = Math.Min(80, stay / 2);
                releaseGap = 50;
                state = state with { Operator = cursor + timing.Resolve(options.OperatorInterval),
                    Return = cursor + stay, ReturnGapMs = stay - hold, Ready = cursor };
            }
            else if (state.MushroomsLeft > 0 || state.Mushrooms <= cursor)
            {
                int left = state.MushroomsLeft;
                if (left == 0)
                {
                    left = options.ActiveMushroomCount;
                    state = state with { Mushrooms = cursor + timing.Resolve(options.MushroomLifetime) -
                        timing.Resolve(options.MushroomLead) };
                }
                bool second = left > options.ActiveMushroom1Count;
                key = second ? options.Mushroom2 : options.Mushroom1;
                name = second ? "Mushroom 2" : "Mushroom 1";
                state = state with
                {
                    MushroomsLeft = left - 1, Ready = cursor + timing.Resolve(options.MushroomCastGap(second)),
                    Setup = state.Setup && (left > 1 || options.RoarEnabled || options.FourthEnabled)
                };
            }
            else
            {
                key = options.Fourth;
                name = "Fourth ability";
                state = state with { Fourth = cursor + timing.Resolve(options.FourthInterval), Ready = cursor, Setup = false };
            }

            var action = new ScenarioAction
            {
                Kind = ActionKind.KeyPress, Key = key, Name = name, Hold = Timing.Fixed(hold),
                Before = Timing.Fixed((int)(cursor - offset))
            };
            actions.Add(action);
            afterPress.Add(action.Id, state);
            if (releaseGap is int gap)
                transferTaps.Add(action.Id, (hold, gap));
            offset = cursor + hold;
        }

        actions.Add(new() { Kind = ActionKind.Wait, Delay = Timing.Fixed((int)Math.Max(0, end - offset)) });
        var plan = compiler.Compile(profile with { Actions = actions.ToImmutable(), ActionGroups = [], Index = null },
            cancellation: cancellation);
        plan = plan with
        {
            Events = plan.Events.Select(input => transferTaps.TryGetValue(input.ActionId, out var tap)
                ? input with { MinimumKeyIntervalMs = input.Type == InputKind.KeyUp ? tap.Hold : tap.ReleaseGap }
                : input).ToImmutableArray()
        };
        return new(plan, initial, afterPress);
    }
}
