using System.Collections.Immutable;

namespace Autocaster.Core;

public enum ActionKind { KeyPress, KeyDown, KeyUp, Wait, Combination, MouseClick, MouseWheel, Sequence, Repeat, KeyHold, Loop, MouseMovement, StartMacroTimer, LarvaDetect, RathuumKillFeed }
public enum CycleMode { Once, Count, Infinite }
public enum RepeatMode { Count, OncePerCycle }
public enum LatePolicy { Dispatch, SkipWheel, Stop }
public enum MouseButton { Left = 1, Right = 2, Middle = 4, X1 = 5, X2 = 6 }

public enum InputKey : ushort
{
    Mouse4 = 5, Mouse5 = 6,
    Backspace = 8, Tab = 9, Enter = 13, Pause = 19, CapsLock = 20, Escape = 27, Space = 32,
    PageUp = 33, PageDown = 34, End = 35, Home = 36, Left = 37, Up = 38, Right = 39, Down = 40,
    PrintScreen = 44, Insert = 45, Delete = 46,
    D0 = 48, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    A = 65, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    LeftWindows = 91, RightWindows = 92, Menu = 93,
    Num0 = 96, Num1, Num2, Num3, Num4, Num5, Num6, Num7, Num8, Num9,
    Multiply, Add, Subtract = 109, Decimal, Divide,
    F1 = 112, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
    NumLock = 144, ScrollLock = 145, LeftShift = 160, RightShift, LeftCtrl, RightCtrl, LeftAlt, RightAlt,
    Semicolon = 186, Plus, Comma, Minus, Period, Slash, Tilde,
    OpenBracket = 219, Backslash, CloseBracket, Quote, Oem102 = 226
}

public readonly record struct Timing(int FixedMs = 0, bool Random = false, int MinMs = 0, int MaxMs = 0, int StepMs = 100)
{
    public static Timing Fixed(int milliseconds) => new(milliseconds);
    public static Timing Range(int minimum, int maximum, int step) => new(0, true, minimum, maximum, step);
    public override string ToString() => Random ? $"{MinMs}–{MaxMs} / {StepMs} ms" : $"{FixedMs} ms";
}

public sealed record ScenarioAction
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required ActionKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
    public bool RunWithPrevious { get; init; }
    public string Name { get; init; } = "";
    public string Comment { get; init; } = "";
    public InputKey Key { get; init; } = InputKey.D1;
    public ImmutableArray<InputKey> Keys { get; init; } = [InputKey.LeftCtrl, InputKey.D1];
    public MouseButton Button { get; init; } = MouseButton.Left;
    public int WheelDelta { get; init; } = 120;
    public ImmutableArray<MouseSample> MousePath { get; init; } = [];
    public int MousePointLimit { get; init; } = MouseRouteBuffer.DefaultPointLimit;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MouseMapPoint? MouseMapOrigin { get; init; }
    public bool ReverseMousePath { get; init; }
    public bool VaryMousePath { get; init; } = true;
    public int? MouseDurationMs { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? MacroTimerDurationMs { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? LarvaTimeoutMs { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RathuumGateOptions? Rathuum { get; init; }
    // Retained for configs saved before the editor used seconds.
    public decimal MouseSpeed { get; init; } = 1;
    public Timing Before { get; init; } = Timing.Fixed(0);
    public Timing After { get; init; } = Timing.Fixed(0);
    public Timing Hold { get; init; } = Timing.Fixed(80);
    public Timing Delay { get; init; } = Timing.Fixed(1000);
    public RepeatMode RepeatMode { get; init; }
    public int RepeatCount { get; init; } = 2;
    public int LoopCount { get; init; }
    public ImmutableArray<ScenarioAction> Children { get; init; } = [];
    public static bool IsHudGate(ActionKind kind) => kind is ActionKind.LarvaDetect or ActionKind.RathuumKillFeed;
    public bool IsGroup => Kind is ActionKind.Sequence or ActionKind.Repeat;
}

public sealed record ActionGroup(Guid Id, string Name, int Start, int Count, int Color = 0);

public sealed record ScenarioProfile
{
    public const int CurrentSchema = 1;
    public required int SchemaVersion { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public CycleMode Mode { get; init; }
    public int CycleCount { get; init; } = 3;
    public Timing BetweenCycles { get; init; } = Timing.Fixed(250);
    public ImmutableArray<ScenarioAction> Actions { get; init; } = [];
    public ImmutableArray<ActionGroup> ActionGroups { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LastRecordedMouseAction { get; init; }
    public MacroOptions? Macro { get; init; }
    public IndexOptions? Index { get; init; }

    public static ScenarioProfile Create(string name) => new() { Name = name, SchemaVersion = CurrentSchema };
}

public sealed record MacroOptions(InputKey Key = InputKey.F7, uint Modifiers = 0, bool Enabled = false, bool Step = false)
{
    public bool UseHotkey { get; init; } = true;
    public bool ShowStepHud { get; init; }
    public bool ShowRathuumWaveSize { get; init; }
    public HudAnchor? StepHudPosition { get; init; }
    public HudAnchor? ArrowPosition { get; init; }
    public bool ShowNextMovement { get; init; }
    public bool ShowTimer { get; init; } = true;
    public MacroTimerOptions? Timer { get; init; }
}

public enum InputKind { KeyDown, KeyUp, MouseButtonDown, MouseButtonUp, MouseWheel, MouseMove }

public readonly record struct InputEvent(InputKind Kind, int Value, int DeltaY = 0)
{
    public bool IsDown => Kind is InputKind.KeyDown or InputKind.MouseButtonDown;
    public bool IsUp => Kind is InputKind.KeyUp or InputKind.MouseButtonUp;
    public InputEvent Release => Kind switch
    {
        InputKind.KeyDown => this with { Kind = InputKind.KeyUp },
        InputKind.MouseButtonDown => this with { Kind = InputKind.MouseButtonUp },
        _ => throw new InvalidOperationException()
    };

    public string Parameter => Kind switch
    {
        InputKind.KeyDown or InputKind.KeyUp => ((InputKey)Value).ToString(),
        InputKind.MouseButtonDown or InputKind.MouseButtonUp => ((MouseButton)Value).ToString(),
        InputKind.MouseMove => $"{Value}, {DeltaY}",
        _ => Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    public override string ToString() => $"{Kind} {Parameter}";
}

public sealed record ScheduledInput(int Number, int OffsetMs, InputEvent Input, Guid ActionId, string Action, bool AutomaticRelease)
{
    public int? TimedHoldMs { get; init; }
    // Critical taps retain their hold and release gaps when the absolute timeline runs late.
    internal int? MinimumKeyIntervalMs { get; init; }
    public InputKind Type => Input.Kind;
    public string Parameter => Input.Parameter;
}
public sealed record ResolvedTiming(Guid ActionId, string Action, string Parameter, int ValueMs, bool Random);
public readonly record struct PlannedAction(Guid Id, int StartMs, int EndMs);
public readonly record struct MacroTimerTrigger(int OffsetMs, int EventsThrough, int? DurationMs = null)
{
    internal int Order { get; init; }
}
public readonly record struct HudGate(Guid ActionId, int OffsetMs, int EventsThrough, int TimeoutMs)
{
    public const int DefaultTimeoutMs = 4000;
    public const int MinTimeoutMs = 100;
    public const int MaxTimeoutMs = 30_000;
    public RathuumGateOptions? Rathuum { get; init; }
    internal int Order { get; init; }
}

public sealed record ExecutionPlan(Guid Id, Guid ScenarioId, DateTimeOffset PreparedAt, int DurationMs,
    ImmutableArray<ScheduledInput> Events, ImmutableArray<ResolvedTiming> Timings, ImmutableArray<string> Notes,
    ImmutableArray<PlannedAction> Actions)
{
    public ImmutableArray<MacroTimerTrigger> TimerTriggers { get; init; } = [];
    public ImmutableArray<HudGate> HudGates { get; init; } = [];

    public Guid? ActionAt(double elapsedMs)
    {
        int low = 0, high = Actions.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (Actions[middle].StartMs <= elapsedMs)
                low = middle + 1;
            else
                high = middle;
        }
        if (low == 0)
            return null;
        var action = Actions[low - 1];
        return elapsedMs < action.EndMs || (action.StartMs == action.EndMs && elapsedMs == action.StartMs)
            ? action.Id : null;
    }
}

public sealed class ScenarioException(string message, Guid? actionId = null) : Exception(message)
{
    public Guid? ActionId { get; } = actionId;
}
