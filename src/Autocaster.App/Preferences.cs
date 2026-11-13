using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Autocaster.Core;
using Autocaster.Infrastructure;
using Autocaster.Windows;

namespace Autocaster.App;

public sealed record Preferences
{
    public bool Dark { get; init; } = true;
    public bool CaptureProtection { get; init; } = true;
    public bool StartInTray { get; init; } = true;
    public bool RestoreProfile { get; init; } = true;
    public bool ActivateGame { get; init; }
    public bool DetailedLog { get; init; }
    public int DefaultHoldMs { get; init; } = 80;
    public int DefaultWaitMs { get; init; } = 1000;
    public int SpinMicroseconds { get; init; } = 250;
    public ScheduleOptions Timing { get; init; } = new();
    public ImmutableArray<Hotkey> Hotkeys { get; init; } =
    [
        new(0, InputKey.F9)
    ];
    public Hotkey? EditorHotkey { get; init; }
    public Hotkey? MacroResetHotkey { get; init; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, InputKey.F10);
    public Hotkey? RathuumWaveHotkey { get; init; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, InputKey.F11);
    public Hotkey? PlaybackResetHotkey { get; init; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, InputKey.F12);
    public ImmutableArray<Guid?> MacroWheelSlots { get; init; } = [.. new Guid?[MacroWheelView.SlotCount]];
    public Guid? LastProfile { get; init; }
    public WindowPlacement? Placement { get; init; }

    internal ImmutableArray<Hotkey> GetHotkeys() => Hotkeys.Add(GetEditorHotkey());

    internal Hotkey GetEditorHotkey()
    {
        if (EditorHotkey is { } configured)
            return configured;
        InputKey[] candidates = [InputKey.F8, InputKey.F7, InputKey.F6, InputKey.F5];
        foreach (var key in candidates)
        {
            var shortcut = new Hotkey(0, key);
            if (!Hotkeys.Contains(shortcut))
                return shortcut;
        }
        throw new InvalidOperationException("No editor hotkey is available.");
    }

    public static async Task<Preferences> Load(string path)
    {
        if (!File.Exists(path))
            return new();
        var value = JsonSerializer.Deserialize<Preferences>(await File.ReadAllBytesAsync(path)) ?? new();
        // The previous format stored Pause after the run shortcut. Older files stored separate Start and Stop keys.
        if (!value.Hotkeys.IsDefault && value.Hotkeys.Length is 2 or 3 or 4)
        {
            var toggle = value.Hotkeys.Length == 2 ? value.Hotkeys[0] : new Hotkey(0, InputKey.F9);
            value = value with { Hotkeys = [toggle], EditorHotkey = value.EditorHotkey == toggle ? null : value.EditorHotkey };
        }
        var record = new Hotkey(0, InputKey.Insert);
        if (!value.Hotkeys.IsDefaultOrEmpty && value.Hotkeys[0] == record)
            value = value with { Hotkeys = [new(0, InputKey.F9)] };
        if (value.EditorHotkey == record || (!value.Hotkeys.IsDefaultOrEmpty && value.EditorHotkey == value.Hotkeys[0]))
            value = value with { EditorHotkey = null };
        value.Validate();
        return value;
    }

    public Task Save(string path) => ProfileService.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(this,
        new JsonSerializerOptions { WriteIndented = true }));

    public void Validate()
    {
        if (DefaultHoldMs is < 0 or > TimingEngine.MaxMilliseconds ||
            DefaultWaitMs is < 0 or > TimingEngine.MaxMilliseconds ||
            SpinMicroseconds is < 0 or > 500 || Hotkeys.IsDefault || Hotkeys.Length != (int)HotkeyAction.ToggleRun ||
            MacroWheelSlots.IsDefault || MacroWheelSlots.Length != MacroWheelView.SlotCount || Timing is null)
            throw new ArgumentException("Invalid application settings.");
        Timing.Validate();
    }
}

public sealed record WindowPlacement(double Left, double Top, double Width, double Height)
{
    internal const int DefaultWidth = 760;
    internal const int DefaultHeight = 515;
}
