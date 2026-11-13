using System.ComponentModel;
using System.Runtime.InteropServices;
using Autocaster.Core;

namespace Autocaster.Windows;

[Flags]
public enum HotkeyModifiers : uint { Alt = 1, Control = 2, Shift = 4, Windows = 8 }

public readonly record struct Hotkey(HotkeyModifiers Modifiers, InputKey Key)
{
    internal bool IsMouse => InputKeys.IsMouse(Key);

    public static HotkeyModifiers ModifierFor(InputKey key) => key switch
    {
        InputKey.LeftAlt or InputKey.RightAlt => HotkeyModifiers.Alt,
        InputKey.LeftCtrl or InputKey.RightCtrl => HotkeyModifiers.Control,
        InputKey.LeftShift or InputKey.RightShift => HotkeyModifiers.Shift,
        InputKey.LeftWindows or InputKey.RightWindows => HotkeyModifiers.Windows,
        _ => 0
    };

    public override string ToString()
    {
        var modifiers = Modifiers;
        string key = Key switch
        {
            InputKey.MouseLeft => "Left click",
            InputKey.MouseRight => "Right click",
            InputKey.MouseMiddle => "Middle click",
            InputKey.Mouse4 => "Mouse 4",
            InputKey.Mouse5 => "Mouse 5",
            InputKey.WheelUp => "Wheel up",
            InputKey.WheelDown => "Wheel down",
            InputKey.LeftAlt => "Left Alt",
            InputKey.RightAlt => "Right Alt",
            InputKey.LeftCtrl => "Left Ctrl",
            InputKey.RightCtrl => "Right Ctrl",
            InputKey.LeftShift => "Left Shift",
            InputKey.RightShift => "Right Shift",
            InputKey.LeftWindows => "Left Windows",
            InputKey.RightWindows => "Right Windows",
            >= InputKey.D0 and <= InputKey.D9 => ((int)Key - (int)InputKey.D0).ToString(),
            _ => Key.ToString()
        };
        return string.Join(" + ", Enum.GetValues<HotkeyModifiers>().Where(value => modifiers.HasFlag(value))
            .Select(value => value == HotkeyModifiers.Control ? "Ctrl" : value.ToString()).Append(key));
    }
}

public enum HotkeyAction { ToggleRun = 1, ToggleEditor, RecordMouse = 100, ResetMacroSteps, ToggleRathuumWaveSize, ResetPlayback }

public sealed class HotkeyService(nint window) : IDisposable
{
    public const int Message = 0x0312;
    private const uint NoRepeat = 0x4000;
    private readonly Dictionary<int, Hotkey> _registered = [];
    private bool _recordingShortcut;
    private readonly Dictionary<Guid, int> _macroIds = [];
    private readonly Dictionary<int, Guid> _macroActions = [];
    private const int FirstMacroId = 1000;
    private Native.HookProcedure? _mouseProcedure;
    private nint _mouseHook;
    private Native.HookProcedure? _keyboardProcedure;
    private nint _keyboardHook;
    private readonly HashSet<InputKey> _heldModifiers = [];
    private Hotkey? _pendingModifier;

    public bool IsRegistered(HotkeyAction action) => _registered.ContainsKey((int)action);

    // Keep working registrations, if a replacement conflicts.
    public IReadOnlyList<string> Configure(IReadOnlyList<Hotkey> desired) =>
        Configure(desired, []);

    public IReadOnlyList<string> ConfigureGlobals(IReadOnlyList<Hotkey> desired, Hotkey? reset) =>
        Configure(desired, [(HotkeyAction.ResetMacroSteps, reset)]);

    public IReadOnlyList<string> ConfigureGlobals(IReadOnlyList<Hotkey> desired, Hotkey? reset, Hotkey? rathuum) =>
        Configure(desired, [(HotkeyAction.ResetMacroSteps, reset), (HotkeyAction.ToggleRathuumWaveSize, rathuum)]);

    public IReadOnlyList<string> ConfigureGlobals(IReadOnlyList<Hotkey> desired, Hotkey? reset, Hotkey? rathuum, Hotkey? playbackReset) =>
        Configure(desired, [(HotkeyAction.ResetMacroSteps, reset), (HotkeyAction.ToggleRathuumWaveSize, rathuum),
            (HotkeyAction.ResetPlayback, playbackReset)]);

    private IReadOnlyList<string> Configure(IReadOnlyList<Hotkey> desired,
        (HotkeyAction Action, Hotkey? Shortcut)[] optional)
    {
        if (desired.Count != (int)HotkeyAction.ToggleEditor || desired.Distinct().Count() != desired.Count)
            return ["Assign two distinct keys or shortcuts."];

        var bindings = desired.Select((key, index) => (Id: index + 1, Key: (Hotkey?)key)).ToList();
        bindings.AddRange(optional.Select(binding => ((int)binding.Action, binding.Shortcut)));
        var assigned = bindings.Select(binding => binding.Key).OfType<Hotkey>().ToArray();
        if (assigned.Any(key => ((uint)key.Modifiers & ~15u) != 0 || !Enum.IsDefined(key.Key)))
            return ["Assign supported keys or shortcuts."];
        if (assigned.Distinct().Count() != assigned.Length)
            return ["Assign distinct global shortcuts."];
        if (assigned.Contains(new Hotkey(0, InputKey.Insert)))
            return ["Insert is reserved for mouse recording."];

        var previous = new Dictionary<int, Hotkey>(_registered);
        var changed = bindings.Where(binding => binding.Key !=
            (_registered.TryGetValue(binding.Id, out var existing) ? existing : (Hotkey?)null)).ToArray();
        foreach (var binding in changed)
            Unregister(binding.Id);

        List<string> errors = [];
        foreach (var (id, key) in changed.Reverse())
        {
            if (key is Hotkey assignedKey && !TryRegister(id, assignedKey, out string error))
                errors.Add(error);
        }
        if (errors.Count == 0)
            return errors;

        foreach (var binding in changed)
            Unregister(binding.Id);
        foreach (var binding in changed.Reverse())
            if (previous.TryGetValue(binding.Id, out var old) && !TryRegister(binding.Id, old, out string error))
                errors.Add(error);
        // On the first launch, still keep any shortcuts that can be registered.
        if (optional.Length == 0 && previous.Count == 0)
            foreach (var (id, key) in changed.Reverse())
                if (key is Hotkey assignedKey)
                    TryRegister(id, assignedKey, out _);
        return errors;
    }

    private bool TryRegister(int id, Hotkey key, out string error)
    {
        if (_registered.ContainsValue(key))
        {
            error = $"Shortcut {key} is already in use.";
            return false;
        }
        bool modifierKey = Hotkey.ModifierFor(key.Key) != 0;
        bool registered = modifierKey ? EnsureModifierHooks()
            : key.IsMouse ? EnsureMouseHook()
            : Native.RegisterHotKey(window, id, (uint)key.Modifiers | NoRepeat, (uint)key.Key);
        if (registered)
        {
            _registered.Add(id, key);
            error = "";
            return true;
        }

        error = $"{key}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
        return false;
    }

    private void Unregister(int id)
    {
        if (!_registered.Remove(id, out var shortcut))
            return;
        if (!shortcut.IsMouse && Hotkey.ModifierFor(shortcut.Key) == 0)
            Native.UnregisterHotKey(window, id);
        if (_keyboardHook != 0 && !_registered.Values.Any(key => Hotkey.ModifierFor(key.Key) != 0))
            RemoveModifierHook();
        if (_mouseHook != 0 && _keyboardHook == 0 && !_registered.Values.Any(key => key.IsMouse))
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
    }

    private bool EnsureModifierHooks()
    {
        if (_keyboardHook != 0)
            return true;

        _keyboardProcedure ??= ObserveKeyboard;
        _keyboardHook = Native.SetWindowsHookExW(13, _keyboardProcedure, Native.GetModuleHandleW(null), 0);
        if (_keyboardHook == 0)
            return false;
        if (EnsureMouseHook())
        {
            foreach (InputKey key in ModifierKeys)
                if (Pressed((int)key))
                    _heldModifiers.Add(key);
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        RemoveModifierHook();
        Marshal.SetLastPInvokeError(error);
        return false;
    }

    private static readonly InputKey[] ModifierKeys =
    [
        InputKey.LeftAlt, InputKey.RightAlt, InputKey.LeftCtrl, InputKey.RightCtrl,
        InputKey.LeftShift, InputKey.RightShift, InputKey.LeftWindows, InputKey.RightWindows
    ];

    private void RemoveModifierHook()
    {
        Native.UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = 0;
        _heldModifiers.Clear();
        _pendingModifier = null;
    }

    private bool EnsureMouseHook()
    {
        const int lowLevelMouse = 14;
        if (_mouseHook == 0)
        {
            _mouseProcedure ??= ObserveMouse;
            _mouseHook = Native.SetWindowsHookExW(lowLevelMouse, _mouseProcedure, Native.GetModuleHandleW(null), 0);
        }
        return _mouseHook != 0;
    }

    private nint ObserveKeyboard(int code, nint message, nint data)
    {
        const int injected = 0x10;
        if (code == 0 && message is 0x0100 or 0x0101 or 0x0104 or 0x0105 &&
            (Marshal.ReadInt32(data, 8) & injected) == 0)
        {
            var key = (InputKey)Marshal.ReadInt32(data);
            bool down = message is 0x0100 or 0x0104;
            var modifier = Hotkey.ModifierFor(key);
            if (modifier == 0)
            {
                if (down)
                    _pendingModifier = null;
            }
            else if (down)
            {
                if (_heldModifiers.Add(key))
                {
                    var shortcut = new Hotkey(ReadModifiers() & ~modifier, key);
                    _pendingModifier = _registered.ContainsValue(shortcut) ? shortcut : null;
                }
            }
            else
            {
                _heldModifiers.Remove(key);
                if (_pendingModifier is { } shortcut && shortcut.Key == key)
                {
                    _pendingModifier = null;
                    Dispatch(shortcut);
                }
            }
        }
        return Native.CallNextHookEx(_keyboardHook, code, message, data);
    }

    private nint ObserveMouse(int code, nint message, nint data)
    {
        const uint injected = 1;
        if (code == 0 && message is 0x0201 or 0x0204 or 0x0207 or 0x020A or 0x020B or 0x020E)
        {
            var input = Marshal.PtrToStructure<Native.LowLevelMouseInput>(data);
            if ((input.Flags & injected) == 0)
            {
                _pendingModifier = null;
                var key = MouseKey((int)message, (short)(input.MouseData >> 16));
                if (key != default && _registered.Values.Any(shortcut => shortcut.Key == key))
                {
                    var shortcut = new Hotkey(ReadModifiers(), key);
                    // A click or scroll in the editor should remain an editor gesture.
                    if (key is InputKey.Mouse4 or InputKey.Mouse5 || !PointsAtEditor(input.Position))
                        Dispatch(shortcut);
                }
            }
        }
        return Native.CallNextHookEx(_mouseHook, code, message, data);
    }

    private static InputKey MouseKey(int message, short data) => message switch
    {
        0x0201 => InputKey.MouseLeft,
        0x0204 => InputKey.MouseRight,
        0x0207 => InputKey.MouseMiddle,
        0x020A when data > 0 => InputKey.WheelUp,
        0x020A when data < 0 => InputKey.WheelDown,
        0x020B when data == 1 => InputKey.Mouse4,
        0x020B when data == 2 => InputKey.Mouse5,
        _ => default
    };

    private static bool PointsAtEditor(Native.Point point)
    {
        Native.GetWindowThreadProcessId(Native.WindowFromPoint(point), out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    private void Dispatch(Hotkey shortcut)
    {
        foreach (var (id, configured) in _registered)
        {
            if (configured != shortcut)
                continue;
            Native.PostMessageW(window, Message, id, ((int)shortcut.Key << 16) | (int)shortcut.Modifiers);
            break;
        }
    }

    private static HotkeyModifiers ReadModifiers()
    {
        HotkeyModifiers modifiers = 0;
        if (Pressed(0x11)) modifiers |= HotkeyModifiers.Control;
        if (Pressed(0x10)) modifiers |= HotkeyModifiers.Shift;
        if (Pressed(0x12)) modifiers |= HotkeyModifiers.Alt;
        if (Pressed(0x5B) || Pressed(0x5C)) modifiers |= HotkeyModifiers.Windows;
        return modifiers;
    }

    private static bool Pressed(int virtualKey) => (Native.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Clear()
    {
        foreach (int id in _registered.Keys.ToArray())
            Unregister(id);
        _macroIds.Clear();
        _macroActions.Clear();
    }

    public bool TryGetMacro(int id, nint parameters, out Guid macro)
    {
        if (_macroActions.TryGetValue(id, out macro) && _registered.TryGetValue(id, out var shortcut))
            return ((uint)parameters & 15u) == (uint)shortcut.Modifiers && ((uint)parameters >> 16) == (uint)shortcut.Key;
        return false;
    }

    public bool IsMessageFor(HotkeyAction action, nint parameters) =>
        _registered.TryGetValue((int)action, out var shortcut) &&
        ((uint)parameters & 15u) == (uint)shortcut.Modifiers && ((uint)parameters >> 16) == (uint)shortcut.Key;

    public string? ConfigureMacroReset(Hotkey? desired) =>
        ConfigureOptional(HotkeyAction.ResetMacroSteps, desired, "Macro reset");

    public string? ConfigureRathuumWaveSize(Hotkey? desired) =>
        ConfigureOptional(HotkeyAction.ToggleRathuumWaveSize, desired, "Rathuum wave size");

    public string? ConfigurePlaybackReset(Hotkey? desired) =>
        ConfigureOptional(HotkeyAction.ResetPlayback, desired, "Playback reset");

    private string? ConfigureOptional(HotkeyAction action, Hotkey? desired, string label)
    {
        int id = (int)action;
        if (desired is not { } key)
        {
            Unregister(id);
            return null;
        }

        bool hadPrevious = _registered.TryGetValue(id, out var previous);
        if (hadPrevious && key == previous)
            return null;
        if (((uint)key.Modifiers & ~15u) != 0 || !Enum.IsDefined(key.Key) ||
            key == new Hotkey(0, InputKey.Insert) || _registered.ContainsValue(key))
            return $"{label} shortcut {key} is invalid or already in use.";

        Unregister(id);
        if (TryRegister(id, key, out string error))
            return null;
        if (hadPrevious && !TryRegister(id, previous, out string restoreError))
            return $"{error} | {restoreError}";
        return error;
    }

    public IReadOnlyList<string> ConfigureMacros(IReadOnlyDictionary<Guid, Hotkey> desired)
    {
        foreach (var (macro, id) in _macroIds.ToArray())
        {
            if (desired.TryGetValue(macro, out var key) && _registered[id] == key)
                continue;
            Unregister(id);
            _macroIds.Remove(macro);
            _macroActions.Remove(id);
        }

        List<string> errors = [];
        int available = FirstMacroId;
        foreach (var (macro, key) in desired)
        {
            if (_macroIds.ContainsKey(macro))
                continue;
            if (key == new Hotkey(0, InputKey.Insert) || _registered.ContainsValue(key))
            {
                errors.Add($"Macro shortcut {key} is already in use.");
                continue;
            }
            while (_registered.ContainsKey(available))
                available++;
            if (available > 0xBFFF)
            {
                errors.Add("Too many registered macros.");
                break;
            }
            if (!TryRegister(available, key, out string error))
                errors.Add(error);
            else
            {
                _macroIds.Add(macro, available);
                _macroActions.Add(available, macro);
                available++;
            }
        }
        return errors;
    }

    public bool ArmMouseRecording(out string error)
    {
        if (_recordingShortcut)
        {
            error = "";
            return true;
        }

        _recordingShortcut = Native.RegisterHotKey(window, (int)HotkeyAction.RecordMouse, NoRepeat, (uint)InputKey.Insert);
        error = _recordingShortcut ? "" : $"Insert: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
        return _recordingShortcut;
    }

    public void DisarmMouseRecording()
    {
        if (_recordingShortcut)
        {
            Native.UnregisterHotKey(window, (int)HotkeyAction.RecordMouse);
            _recordingShortcut = false;
        }
    }

    public void Dispose()
    {
        DisarmMouseRecording();
        Clear();
    }
}
