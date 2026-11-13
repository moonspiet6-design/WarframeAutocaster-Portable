using System.ComponentModel;
using System.Runtime.InteropServices;
using Autocaster.Core;

namespace Autocaster.Windows;

[Flags]
public enum HotkeyModifiers : uint { Alt = 1, Control = 2, Shift = 4, Windows = 8 }

public readonly record struct Hotkey(HotkeyModifiers Modifiers, InputKey Key)
{
    internal bool IsMouseButton => Key is InputKey.Mouse4 or InputKey.Mouse5;

    public override string ToString()
    {
        var modifiers = Modifiers;
        string key = Key switch
        {
            InputKey.Mouse4 => "Mouse 4",
            InputKey.Mouse5 => "Mouse 5",
            >= InputKey.D0 and <= InputKey.D9 => ((int)Key - (int)InputKey.D0).ToString(),
            _ => Key.ToString()
        };
        return string.Join(" + ", Enum.GetValues<HotkeyModifiers>().Where(value => modifiers.HasFlag(value))
            .Select(value => value == HotkeyModifiers.Control ? "Ctrl" : value.ToString()).Append(key));
    }
}

public enum HotkeyAction { ToggleEditor = 2, RecordMouse = 100, ResetMacroSteps, ToggleRathuumWaveSize, ResetPlayback }

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

    public bool IsRegistered(HotkeyAction action) => _registered.ContainsKey((int)action);

    // Keep working registrations, if a replacement conflicts.
    public IReadOnlyList<string> ConfigureGlobals(Hotkey editor, Hotkey? reset, Hotkey? rathuum, Hotkey? playbackReset)
    {
        (int Id, Hotkey? Key)[] bindings =
        [
            ((int)HotkeyAction.ToggleEditor, editor), ((int)HotkeyAction.ResetMacroSteps, reset),
            ((int)HotkeyAction.ToggleRathuumWaveSize, rathuum), ((int)HotkeyAction.ResetPlayback, playbackReset)
        ];
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
        return errors;
    }

    private bool TryRegister(int id, Hotkey key, out string error)
    {
        if (_registered.ContainsValue(key))
        {
            error = $"Shortcut {key} is already in use.";
            return false;
        }
        if (key.IsMouseButton ? EnsureMouseHook() : Native.RegisterHotKey(window, id, (uint)key.Modifiers | NoRepeat, (uint)key.Key))
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
        if (!shortcut.IsMouseButton)
            Native.UnregisterHotKey(window, id);
        else if (_mouseHook != 0 && !_registered.Values.Any(key => key.IsMouseButton))
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
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

    private nint ObserveMouse(int code, nint message, nint data)
    {
        const int sideButtonDown = 0x020B;
        const uint injected = 1;
        if (code == 0 && message == sideButtonDown)
        {
            var input = Marshal.PtrToStructure<Native.LowLevelMouseInput>(data);
            var key = (input.MouseData >> 16) switch
            {
                1 => InputKey.Mouse4,
                2 => InputKey.Mouse5,
                _ => default
            };
            if (key != default && (input.Flags & injected) == 0)
            {
                HotkeyModifiers modifiers = 0;
                if (Pressed(0x11)) modifiers |= HotkeyModifiers.Control;
                if (Pressed(0x10)) modifiers |= HotkeyModifiers.Shift;
                if (Pressed(0x12)) modifiers |= HotkeyModifiers.Alt;
                if (Pressed(0x5B) || Pressed(0x5C)) modifiers |= HotkeyModifiers.Windows;

                var shortcut = new Hotkey(modifiers, key);
                foreach (var (id, configured) in _registered)
                {
                    if (configured != shortcut)
                        continue;
                    Native.PostMessageW(window, Message, id, ((int)key << 16) | (int)modifiers);
                    break;
                }
            }
        }
        return Native.CallNextHookEx(_mouseHook, code, message, data);

        static bool Pressed(int virtualKey) => (Native.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

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
