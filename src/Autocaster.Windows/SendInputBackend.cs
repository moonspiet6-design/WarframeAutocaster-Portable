using System.ComponentModel;
using System.Runtime.InteropServices;
using Autocaster.Core;

namespace Autocaster.Windows;

public sealed class SendInputBackend : IInputBackend
{
    private static readonly int InputSize = Marshal.SizeOf<Native.Input>();
    private readonly Dictionary<int, Native.KeyboardInput> _keyboardDown = [];

    public bool IsPressed(InputEvent input) => (Native.GetAsyncKeyState(input.Value) & 0x8000) != 0;

    public void Send(InputEvent input)
    {
        var native = input.Kind is InputKind.KeyDown or InputKind.KeyUp
            ? Keyboard(input)
            : Mouse(input);

        // A single INPUT has no partially delivered prefix: the return is either one or zero.
        if (Native.SendInput(1, in native, InputSize) != 1)
        {
            int code = Marshal.GetLastPInvokeError();
            string detail = code == 0 ? "Windows did not report a reason." : new Win32Exception(code).Message;
            throw new Win32Exception(code, $"SendInput ({input}): {detail} Check whether the target window runs with elevated privileges.");
        }

        if (input.Kind == InputKind.KeyDown)
            _keyboardDown.Add(input.Value, native.Data.Keyboard);
        if (input.Kind == InputKind.KeyUp)
            _keyboardDown.Remove(input.Value);
    }

    private Native.Input Keyboard(InputEvent input)
    {
        const uint extended = 0x0001, keyUp = 0x0002, scanCode = 0x0008;
        var key = (InputKey)input.Value;
        Native.KeyboardInput keyboard;
        if (input.IsUp && _keyboardDown.TryGetValue(input.Value, out var down))
        {
            keyboard = down;
            keyboard.Flags |= keyUp;
        }
        else
        {
            nint window = Native.GetForegroundWindow();
            uint thread = Native.GetWindowThreadProcessId(window, out _);
            uint scan = Native.MapVirtualKeyExW((uint)key, 4, Native.GetKeyboardLayout(thread));
            uint prefix = scan >> 8;
            keyboard = scan != 0 && prefix is 0 or 0xE0 && key is not InputKey.Pause and not InputKey.PrintScreen
                ? new() { ScanCode = (ushort)(scan & 0xff), Flags = scanCode | (prefix == 0xE0 ? extended : 0) }
                : new() { VirtualKey = (ushort)key, Flags = prefix == 0xE0 ? extended : 0 };
            if (input.IsUp)
                keyboard.Flags |= keyUp;
        }

        return new() { Type = 1, Data = new() { Keyboard = keyboard } };
    }

    private static Native.Input Mouse(InputEvent input)
    {
        const uint move = 0x0001, noCoalesce = 0x2000;
        if (input.Kind == InputKind.MouseMove)
            return new() { Data = new() { Mouse = new() { X = input.Value, Y = input.DeltaY, Flags = move | noCoalesce } } };

        uint flags;
        uint data = 0;
        if (input.Kind == InputKind.MouseWheel)
        {
            flags = 0x0800;
            data = unchecked((uint)input.Value);
        }
        else
        {
            var button = (MouseButton)input.Value;
            flags = button switch
            {
                MouseButton.Left => 0x0002,
                MouseButton.Right => 0x0008,
                MouseButton.Middle => 0x0020,
                MouseButton.X1 or MouseButton.X2 => 0x0080,
                _ => throw new ArgumentOutOfRangeException(nameof(input))
            };
            if (input.IsUp)
                flags <<= 1;
            if (button is MouseButton.X1 or MouseButton.X2)
                data = button == MouseButton.X1 ? 1u : 2u;
        }
        return new() { Data = new() { Mouse = new() { Flags = flags, MouseData = data } } };
    }
}
