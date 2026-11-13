using System.Diagnostics;
using Autocaster.Core;

namespace Autocaster.Windows;

public sealed class WindowsDeadlineClock : IDeadlineClock, IDisposable
{
    private readonly StopwatchClock _fallback = new();
    private readonly EventWaitHandle? _timer;
    private int _precisionMicroseconds = 250;

    public WindowsDeadlineClock()
    {
        var timer = Native.CreateWaitableTimerExW(0, null, 0x00000002, 0x00100002);
        if (timer.IsInvalid)
        {
            timer.Dispose();
            return;
        }
        _timer = new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = timer };
    }

    public int PrecisionMicroseconds
    {
        get => Volatile.Read(ref _precisionMicroseconds);
        set => Volatile.Write(ref _precisionMicroseconds, Math.Clamp(value, 0, 500));
    }

    public bool HighResolutionAvailable => _timer is not null;
    public long Frequency => Stopwatch.Frequency;
    public long Timestamp => Stopwatch.GetTimestamp();

    public void WaitUntil(long deadline, CancellationToken cancellation)
    {
        if (_timer is null)
        {
            _fallback.WaitUntil(deadline, cancellation);
            return;
        }

        long spinTicks = Frequency * PrecisionMicroseconds / 1_000_000;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            long remaining = deadline - Timestamp;
            if (remaining <= 0)
                return;
            if (remaining <= spinTicks)
            {
                Thread.SpinWait(12);
                continue;
            }

            long due = -Math.Max(1, (long)((remaining - spinTicks) * 10_000_000d / Frequency));
            if (!Native.SetWaitableTimerEx(_timer.SafeWaitHandle, in due, 0, 0, 0, 0, 0))
            {
                _fallback.WaitUntil(deadline, cancellation);
                return;
            }
            WaitHandle.WaitAny([cancellation.WaitHandle, _timer]);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
