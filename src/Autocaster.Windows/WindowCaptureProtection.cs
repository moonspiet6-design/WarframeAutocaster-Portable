using System.Runtime.InteropServices;

namespace Autocaster.Windows;

/// <summary>Applies capture exclusion to top-level windows on the current GUI thread before they are shown.</summary>
public sealed class WindowCaptureProtection : IDisposable
{
    private const uint ExcludeFromCapture = 0x11;
    private const uint ShowWindow = 0x0018;
    private const uint DestroyWindow = 0x0082;
    private const uint WindowPosChanging = 0x0046;
    private const uint ShowWindowPosition = 0x0040;
    private const int CallWndProc = 4;
    private readonly Native.HookProcedure _procedure;
    private static readonly HashSet<nint> Windows = [];
    private static bool _enabled = true;
    private nint _hook;

    public WindowCaptureProtection()
    {
        _procedure = BeforeWindowMessage;
        _hook = Native.SetWindowsHookExW(CallWndProc, _procedure, 0, Native.GetCurrentThreadId());
        InstallationError = _hook == 0 ? Marshal.GetLastPInvokeError() : 0;
    }

    public int InstallationError { get; }

    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        foreach (nint window in Windows.ToArray())
            if (Native.IsWindow(window))
                TryExclude(window);
            else
                Windows.Remove(window);
    }

    public static bool TryExclude(nint window)
    {
        if (!IsTopLevel(window))
            return false;
        Windows.Add(window);
        uint affinity = _enabled ? ExcludeFromCapture : 0;
        return (Native.GetWindowDisplayAffinity(window, out uint current) && current == affinity) ||
            Native.SetWindowDisplayAffinity(window, affinity);
    }

    public static bool IsExcluded(nint window) =>
        Native.GetWindowDisplayAffinity(window, out uint affinity) && affinity == ExcludeFromCapture;

    private nint BeforeWindowMessage(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && lParam != 0)
        {
            var message = Marshal.PtrToStructure<Native.CallWindowMessage>(lParam);
            if (message.Message == DestroyWindow)
                Windows.Remove(message.Window);
            if (message.Message == ShowWindow && message.WParam != 0 ||
                message.Message == WindowPosChanging && message.LParam != 0 &&
                (Marshal.PtrToStructure<Native.WindowPosition>(message.LParam).Flags & ShowWindowPosition) != 0)
                TryExclude(message.Window);
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsTopLevel(nint window) => window != 0 &&
        Native.GetAncestor(window, 2) == window && (Native.GetWindowLongW(window, -16) & 0x40000000) == 0;

    public void Dispose()
    {
        if (_hook != 0)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }
}

