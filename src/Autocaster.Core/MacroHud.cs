using System.Collections.Concurrent;

namespace Autocaster.Core;

public readonly record struct HudAnchor(decimal X, decimal Y)
{
    internal bool IsValid => X is >= 0 and <= 1 && Y is >= 0 and <= 1;
}

public sealed record MacroTimerOptions
{
    public const int DefaultFontSize = 22;
    public const int MinFontSize = 12;
    public const int MaxFontSize = 80;

    public int DurationMs { get; init; } = 30_000;
    public HudAnchor Position { get; init; } = new(.5m, .84m);
    public int FontSize { get; init; } = DefaultFontSize;

    internal void Validate()
    {
        if (DurationMs is < 1 or > TimingEngine.MaxMilliseconds || !Position.IsValid ||
            FontSize is < MinFontSize or > MaxFontSize)
            throw new ScenarioException("Check the HUD timer duration, position, and size.");
    }
}

public readonly record struct MacroCountdown(Guid MacroId, int RemainingMs, int DurationMs, HudAnchor Position,
    int FontSize = MacroTimerOptions.DefaultFontSize)
{
    public bool ShowsMilliseconds => DurationMs < 60_000;
}

public sealed class MacroHudTimers(TimeProvider? timeProvider = null)
{
    private sealed record Timer(long Started, MacroTimerOptions Options);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, Timer> _timers = new();

    public void Start(Guid macroId, MacroTimerOptions options)
    {
        options.Validate();
        _timers[macroId] = new(_clock.GetTimestamp(), options);
    }

    public void Stop(Guid macroId) => _timers.TryRemove(macroId, out _);

    public void Clear() => _timers.Clear();

    public MacroCountdown? Read(Guid macroId)
    {
        while (_timers.TryGetValue(macroId, out var timer))
        {
            double remaining = timer.Options.DurationMs - _clock.GetElapsedTime(timer.Started).TotalMilliseconds;
            if (remaining > 0)
                return new(macroId, (int)Math.Ceiling(remaining), timer.Options.DurationMs, timer.Options.Position, timer.Options.FontSize);
            if (_timers.TryRemove(new KeyValuePair<Guid, Timer>(macroId, timer)))
                return null;
        }
        return null;
    }
}

public readonly record struct MouseMovementHint(Guid ActionId, long DeltaX, long DeltaY)
{
    public double AngleDegrees => Math.Atan2(DeltaY, DeltaX) * 180 / Math.PI;

    public static MouseMovementHint? From(ScenarioAction action)
    {
        if (action.Kind != ActionKind.MouseMovement || action.MousePath.IsDefaultOrEmpty)
            return null;

        long x = 0, y = 0, farX = 0, farY = 0;
        double farDistance = 0;
        foreach (var sample in action.MousePath)
        {
            x += sample.DeltaX;
            y += sample.DeltaY;
            double distance = (double)x * x + (double)y * y;
            if (distance > farDistance)
            {
                farDistance = distance;
                farX = x;
                farY = y;
            }
        }

        // A reversing route points toward its outbound destination. A closed
        // forward route uses the point furthest from its starting position.
        if (x != 0 || y != 0)
            return new(action.Id, x, y);
        return farDistance > 0 ? new(action.Id, farX, farY) : null;
    }
}

