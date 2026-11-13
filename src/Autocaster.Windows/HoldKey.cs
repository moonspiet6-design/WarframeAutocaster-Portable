using System.ComponentModel;
using System.Runtime.InteropServices;
using Autocaster.Core;

namespace Autocaster.Windows;

public sealed class HoldKey(nint window, InputKey key, Func<bool> canBegin) : IDisposable
{
    public const int Message = 0x8031;
    private Native.HookProcedure? _procedure;
    private nint _hook;
    private bool _enabled;
    private bool _pressed;
    private bool _captured;

    public bool IsHeld => _captured;

    public string? SetEnabled(bool enabled)
    {
        if (enabled && _hook == 0)
        {
            _procedure ??= Observe;
            _hook = Native.SetWindowsHookExW(13, _procedure, Native.GetModuleHandleW(null), 0);
            if (_hook == 0)
                return new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        }
        _enabled = enabled;
        return null;
    }

    private nint Observe(int code, nint message, nint data)
    {
        if (code == 0 && message is 0x0100 or 0x0101 or 0x0104 or 0x0105 &&
            Marshal.ReadInt32(data) == (int)key && (Marshal.ReadInt32(data, 8) & 0x10) == 0)
        {
            if (message is 0x0101 or 0x0105)
            {
                _pressed = false;
                if (_captured)
                {
                    _captured = false;
                    Native.PostMessageW(window, Message, 0, 0);
                    return 1;
                }
            }
            else
            {
                if (!_pressed)
                {
                    _pressed = true;
                    _captured = _enabled && NoModifiers() && canBegin() &&
                        Native.PostMessageW(window, Message, 1, 0);
                }
                if (_captured)
                    return 1;
            }
        }
        return Native.CallNextHookEx(_hook, code, message, data);
    }

    private static bool NoModifiers() => !Pressed(0x10) && !Pressed(0x11) && !Pressed(0x12) &&
        !Pressed(0x5B) && !Pressed(0x5C);

    private static bool Pressed(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hook == 0)
            return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = 0;
        _enabled = _pressed = _captured = false;
    }
}
