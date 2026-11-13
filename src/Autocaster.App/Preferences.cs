using System;
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
    public bool StartInTray { get; init; }
    public bool RestoreProfile { get; init; } = true;
    public bool DetailedLog { get; init; }
    public int DefaultHoldMs { get; init; } = 80;
    public int DefaultWaitMs { get; init; } = 1000;
    public int SpinMicroseconds { get; init; } = 250;
    public ScheduleOptions Timing { get; init; } = new();
    public Hotkey EditorHotkey { get; init; } = new(0, InputKey.F8);
    public Hotkey? MacroResetHotkey { get; init; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, InputKey.F10);
    public Hotkey? RathuumWaveHotkey { get; init; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, InputKey.F11);
    public Hotkey? PlaybackResetHotkey { get; init; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, InputKey.F12);
    public Guid? LastProfile { get; init; }
    public WindowPlacement? Placement { get; init; }

    public static async Task<Preferences> Load(string path)
    {
        if (!File.Exists(path))
            return new();
        var value = JsonSerializer.Deserialize<Preferences>(await File.ReadAllBytesAsync(path)) ?? new();
        value.Validate();
        return value;
    }

    public Task Save(string path) => ProfileService.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(this,
        new JsonSerializerOptions { WriteIndented = true }));

    public void Validate()
    {
        if (DefaultHoldMs is < 0 or > TimingEngine.MaxMilliseconds ||
            DefaultWaitMs is < 0 or > TimingEngine.MaxMilliseconds ||
            SpinMicroseconds is < 0 or > 500 || Timing is null || !Enum.IsDefined(EditorHotkey.Key))
            throw new ArgumentException("Invalid application settings.");
        Timing.Validate();
    }
}

public sealed record WindowPlacement(double Left, double Top, double Width, double Height)
{
    internal const int DefaultWidth = 760;
    internal const int DefaultHeight = 515;
}
