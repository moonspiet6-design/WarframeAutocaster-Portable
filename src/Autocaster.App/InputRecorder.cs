using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Autocaster.Core;
using Autocaster.Windows;
using InputMouseButton = Autocaster.Core.MouseButton;
using MouseButton = System.Windows.Input.MouseButton;

namespace Autocaster.App;

internal sealed class InputRecorder
{
    private enum Mode { Shortcut, Key, Mouse }

    private sealed record Field(Button Button, Func<string> Display, Mode Mode,
        Action<Hotkey>? Keyboard, Action<InputMouseButton>? Mouse);

    private readonly Action<bool> _capture;
    private readonly Func<ModifierKeys> _modifiers;
    private Field? _active;
    private Hotkey? _pendingKey;
    private Key _pressedKey;
    private MouseButton? _pressedButton;

    internal InputRecorder(Window window, Action<bool> capture, Func<ModifierKeys>? modifiers = null)
    {
        _capture = capture;
        _modifiers = modifiers ?? (() => Keyboard.Modifiers);
        window.PreviewKeyDown += OnKeyDown;
        window.PreviewKeyUp += OnKeyUp;
        window.PreviewMouseDown += OnMouseDown;
        window.PreviewMouseUp += OnMouseUp;
        window.Deactivated += (_, _) => Finish();
        window.Closed += (_, _) => Finish();
    }

    internal Func<Hotkey> AddShortcut(Panel panel, string label, Hotkey current, Action? edited = null)
    {
        var read = AddShortcut(panel, label, (Hotkey?)current, edited);
        return () => read() ?? current;
    }

    internal Func<Hotkey?> AddShortcut(Panel panel, string label, Hotkey? current, Action? edited = null)
    {
        var value = current;
        var button = ShortcutField(label, () => value, key =>
        {
            value = key;
            edited?.Invoke();
        });
        var row = new DockPanel { Margin = new(0, 0, 0, 4) };
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(row);
        return () => value;
    }

    internal Button ShortcutField(string label, Func<Hotkey?> display, Action<Hotkey> assign, string? prompt = null) =>
        CreateField(label, () => display()?.ToString() ?? "Unassigned", Mode.Shortcut, assign, prompt: prompt);

    internal Button KeyField(string label, Func<string> display, Action<InputKey> assign) =>
        CreateField(label, display, Mode.Key, key => assign(key.Key));

    internal Button MouseField(string label, Func<string> display, Action<InputMouseButton> assign) =>
        CreateField(label, display, Mode.Mouse, mouse: assign);

    private Button CreateField(string label, Func<string> display, Mode mode,
        Action<Hotkey>? keyboard = null, Action<InputMouseButton>? mouse = null, string? prompt = null)
    {
        var button = new Button { Content = display(), MinWidth = 130, Padding = new(9, 4, 9, 4) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarAction");
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            Finish();
            _active = new(button, display, mode, keyboard, mouse);
            button.Content = prompt ?? (mode switch
            {
                Mode.Mouse => "Press a mouse button…",
                Mode.Shortcut => "Press a key or side button…",
                _ => "Press a key…"
            });
            _capture(true);
            Keyboard.Focus(button);
        };
        button.LostKeyboardFocus += (_, _) =>
        {
            if (_active?.Button == button)
                Finish();
        };
        return button;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_active is not { } active)
            return;
        e.Handled = true;
        if (e.IsRepeat || _pendingKey.HasValue || _pressedButton.HasValue)
            return;

        var key = ActualKey(e);
        if (active.Mode == Mode.Mouse)
        {
            if (key == Key.Escape)
                Finish();
            return;
        }

        var modifiers = _modifiers();
        if (active.Mode == Mode.Shortcut)
        {
            if (key == Key.Escape && modifiers == ModifierKeys.None)
            {
                Finish();
                return;
            }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
                Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
                return;
        }
        var input = (InputKey)KeyInterop.VirtualKeyFromKey(key);
        if (!Enum.IsDefined(input))
        {
            active.Button.Content = "Try another key…";
            return;
        }

        _pendingKey = new(active.Mode == Mode.Shortcut ? ConvertModifiers(modifiers) : 0, input);
        _pressedKey = key;
        active.Button.Content = active.Mode == Mode.Shortcut ? _pendingKey.Value.ToString() : Words.Key(input);
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (_active is not { } active)
            return;
        e.Handled = true;
        if (_pressedButton is null && _pendingKey is { } key && ActualKey(e) == _pressedKey)
        {
            active.Keyboard?.Invoke(key);
            Finish();
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_active is not { } active)
            return;
        bool sideShortcut = active.Mode == Mode.Shortcut && e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2;
        if (active.Mode != Mode.Mouse && !sideShortcut)
            return;
        e.Handled = true;
        if (_pressedButton is not null || _pendingKey is not null)
            return;
        _pressedButton = e.ChangedButton;
        if (sideShortcut)
        {
            var key = e.ChangedButton == MouseButton.XButton1 ? InputKey.Mouse4 : InputKey.Mouse5;
            _pendingKey = new(ConvertModifiers(_modifiers()), key);
            active.Button.Content = _pendingKey.Value.ToString();
        }
        else
            active.Button.Content = Words.Mouse(ConvertMouse(e.ChangedButton));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_active is not { } active || (active.Mode != Mode.Mouse && _pressedButton != e.ChangedButton))
            return;
        e.Handled = true;
        if (_pressedButton == e.ChangedButton)
        {
            if (_pendingKey is { } shortcut)
                active.Keyboard?.Invoke(shortcut);
            else
                active.Mouse?.Invoke(ConvertMouse(e.ChangedButton));
            Finish();
        }
    }

    private void Finish()
    {
        if (_active is not { } active)
            return;
        _active = null;
        _pendingKey = null;
        _pressedButton = null;
        active.Button.Content = active.Display();
        _capture(false);
    }

    private static HotkeyModifiers ConvertModifiers(ModifierKeys modifiers)
    {
        var flags = (HotkeyModifiers)0;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Windows)) flags |= HotkeyModifiers.Windows;
        return flags;
    }

    private static InputMouseButton ConvertMouse(MouseButton button) => button switch
    {
        MouseButton.Left => InputMouseButton.Left,
        MouseButton.Right => InputMouseButton.Right,
        MouseButton.Middle => InputMouseButton.Middle,
        MouseButton.XButton1 => InputMouseButton.X1,
        MouseButton.XButton2 => InputMouseButton.X2,
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    private static Key ActualKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key
    };
}
