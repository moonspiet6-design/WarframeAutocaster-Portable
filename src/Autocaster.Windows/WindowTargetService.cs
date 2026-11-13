using System.ComponentModel;
using System.Diagnostics;
using Autocaster.Core;

namespace Autocaster.Windows;

public class WindowTargetService(nint testWindow = default, Func<bool>? inputAllowed = null) : IWindowTarget
{
    private static readonly string[] GameProcesses = ["Warframe.x64", "Warframe"];
    private nint _window;
    private uint _processId;

    public bool TryArm()
    {
        if (inputAllowed?.Invoke() == false)
            return false;
        nint foreground = Native.GetForegroundWindow();
        if (foreground == 0)
            return false;

        if (_window != 0 && HasFocus())
            return true;

        Native.GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == 0)
            return false;

        if (testWindow != 0)
        {
            if (foreground != testWindow || pid != (uint)Environment.ProcessId)
                return false;
        }
        else
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.HasExited || !GameProcesses.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase) ||
                    process.MainWindowHandle != foreground)
                    return false;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return false; // The foreground process can exit or be inaccessible during inspection.
            }
        }

        _processId = pid;
        _window = foreground;
        return HasFocus();
    }

    public bool HasFocus()
    {
        if (inputAllowed?.Invoke() == false || _window == 0 || Native.GetForegroundWindow() != _window || !Native.IsWindow(_window))
            return false;
        Native.GetWindowThreadProcessId(_window, out uint pid);
        return pid == _processId;
    }

    public void VerifyPointer(InputEvent input)
    {
        if (input.Kind is not (InputKind.MouseButtonDown or InputKind.MouseWheel))
            return;
        if (!Native.GetCursorPos(out var point) || Native.GetAncestor(Native.WindowFromPoint(point), 2) != _window)
            throw new InvalidOperationException("The pointer is outside the target window. Mouse input was canceled.");
    }

    protected bool TryGetClientArea(out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (!HasFocus() || !ReadClientBounds(_window, out var bounds))
            return false;
        (x, y, width, height) = (bounds.X, bounds.Y, bounds.Width, bounds.Height);
        return true;
    }

    protected bool TryMovePointer(int x, int y)
    {
        var point = new Native.Point { X = x, Y = y };
        return HasFocus() && Native.GetAncestor(Native.WindowFromPoint(point), 2) == _window &&
            Native.SetCursorPos(x, y) && PointerAt(x, y);
    }

    protected static bool PointerAt(int x, int y) =>
        Native.GetCursorPos(out var point) && point.X == x && point.Y == y;

    public static bool GameHasFocus() => new WindowTargetService().TryArm();

    public bool TryGetFocusedClientBounds(out System.Drawing.Rectangle bounds)
    {
        bounds = default;
        if (!TryArm() || !TryGetClientArea(out int x, out int y, out int width, out int height))
            return false;
        bounds = new(x, y, width, height);
        return true;
    }

    public bool TryGetGameClientBounds(out System.Drawing.Rectangle bounds)
    {
        Native.GetWindowThreadProcessId(_window, out uint pid);
        if (pid != 0 && pid == _processId && ReadClientBounds(_window, out bounds))
            return true;
        foreach (string name in GameProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (ReadClientBounds(process.MainWindowHandle, out bounds))
                            return true;
                    }
                    catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                    {
                        // The game can close while its window is being inspected.
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        bounds = default;
        return false;
    }

    private static bool ReadClientBounds(nint window, out System.Drawing.Rectangle bounds)
    {
        bounds = default;
        var origin = new Native.Point();
        if (window == 0 || !Native.IsWindowVisible(window) || Native.IsIconic(window) ||
            !Native.GetClientRect(window, out var area) || !Native.ClientToScreen(window, ref origin) ||
            area.Right <= 0 || area.Bottom <= 0)
            return false;
        bounds = new(origin.X, origin.Y, area.Right, area.Bottom);
        return true;
    }

    public static bool RequestGameActivation()
    {
        foreach (string name in GameProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        nint window = process.MainWindowHandle;
                        if (window != 0 && Native.IsWindow(window) && Native.SetForegroundWindow(window))
                            return Native.GetForegroundWindow() == window;
                    }
                    catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                    {
                        // Keep looking if this instance exited or is inaccessible.
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        return false;
    }
}
