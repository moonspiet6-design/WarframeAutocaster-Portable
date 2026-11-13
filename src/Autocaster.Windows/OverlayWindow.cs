namespace Autocaster.Windows;

public static class OverlayWindow
{
    private const int ExtendedStyle = -20;
    private const int ToolWindow = 0x00000080;
    private const int AppWindow = 0x00040000;
    private const int NoActivate = 0x08000000;
    private const int Transparent = 0x00000020;

    public static void ExcludeFromTaskSwitcher(nint window)
    {
        int style = Native.GetWindowLongW(window, ExtendedStyle);
        Native.SetWindowLongW(window, ExtendedStyle, (style | ToolWindow) & ~AppWindow);
    }

    public static bool IsExcludedFromTaskSwitcher(nint window) =>
        (Native.GetWindowLongW(window, ExtendedStyle) & (ToolWindow | AppWindow)) == ToolWindow;

    public static void MakePassive(nint window)
    {
        int style = Native.GetWindowLongW(window, ExtendedStyle);
        Native.SetWindowLongW(window, ExtendedStyle, (style | ToolWindow | NoActivate | Transparent) & ~AppWindow);
    }

    public static bool TryActivate(nint window)
    {
        if (Native.GetForegroundWindow() == window || Native.SetForegroundWindow(window))
            return Native.GetForegroundWindow() == window;

        uint current = Native.GetCurrentThreadId();
        uint foreground = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        if (foreground == 0 || current == foreground || !Native.AttachThreadInput(current, foreground, true))
            return false;
        try { return Native.SetForegroundWindow(window) && Native.GetForegroundWindow() == window; }
        finally { Native.AttachThreadInput(current, foreground, false); }
    }

    public static void ReleasePointer() => Native.ClipCursor(0);

    public static bool Place(nint window, System.Drawing.Rectangle bounds) =>
        Native.SetWindowPos(window, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010);
}
