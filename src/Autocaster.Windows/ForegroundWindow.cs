using System.Runtime.InteropServices;

namespace Autocaster.Windows;

public readonly struct ForegroundWindow
{
    private readonly nint _handle;
    private readonly uint _threadId;
    private readonly uint _processId;

    private ForegroundWindow(nint handle, uint threadId, uint processId) =>
        (_handle, _threadId, _processId) = (handle, threadId, processId);

    public static bool IsOwnWindowActive
    {
        get
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint processId);
            return processId == (uint)Environment.ProcessId;
        }
    }

    public static ForegroundWindow CaptureExternal()
    {
        nint handle = Native.GetForegroundWindow();
        uint threadId = Native.GetWindowThreadProcessId(handle, out uint processId);
        return threadId == 0 || processId == (uint)Environment.ProcessId
            ? default : new(handle, threadId, processId);
    }

    public void TryActivate()
    {
        if (IsAvailable())
            Native.SetForegroundWindow(_handle);
    }

    public void MoveToSameMonitor(nint window, bool nearTop = false)
    {
        const uint nearest = 2, noSize = 0x0001, noZOrder = 0x0004, noActivate = 0x0010;
        if (window == 0 || !IsAvailable())
            return;
        nint monitor = Native.MonitorFromWindow(_handle, nearest);
        if (monitor == 0 || (!nearTop && monitor == Native.MonitorFromWindow(window, nearest)))
            return;
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfoW(monitor, ref info) || !Native.GetWindowRect(window, out var bounds))
            return;

        int left = info.Work.Left + Math.Max(0, (info.Work.Right - info.Work.Left - (bounds.Right - bounds.Left)) / 2);
        int top = info.Work.Top + (nearTop ? 12 : Math.Max(0, (info.Work.Bottom - info.Work.Top - (bounds.Bottom - bounds.Top)) / 2));
        Native.SetWindowPos(window, 0, left, top, 0, 0, noSize | noZOrder | noActivate);
    }

    private bool IsAvailable() => _handle != 0 && Native.IsWindowVisible(_handle) && !Native.IsIconic(_handle) &&
        Native.GetWindowThreadProcessId(_handle, out uint processId) == _threadId && processId == _processId;
}
