using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Autocaster.Core;

namespace Autocaster.Windows;

public sealed class RawMouseCapture(nint window) : IDisposable
{
    public const int InputMessage = 0x00FF;
    private const ushort GenericDesktop = 1;
    private const ushort Mouse = 2;
    private const uint Remove = 1;
    private const uint InputSink = 0x100;
    private const uint InputData = 0x10000003;
    private const ushort Absolute = 1;
    private static readonly uint DeviceSize = (uint)Marshal.SizeOf<Native.RawInputDevice>();
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<Native.RawInputHeader>();
    private static readonly uint PacketSize = (uint)Marshal.SizeOf<Native.RawMouseInput>();
    private MouseRouteBuffer? _buffer;
    private long _start;

    public bool Recording => _buffer is not null;
    public bool TimeLimitReached => Recording && Stopwatch.GetElapsedTime(_start).TotalMilliseconds >= MouseRoute.MaxDurationMs;

    public void Start()
    {
        if (Recording)
            return;
        var device = new Native.RawInputDevice { UsagePage = GenericDesktop, Usage = Mouse, Flags = InputSink, Window = window };
        if (!Native.RegisterRawInputDevices(in device, 1, DeviceSize))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not start mouse recording.");
        _start = Stopwatch.GetTimestamp();
        _buffer = new();
    }

    public bool Read(nint rawInput)
    {
        if (_buffer is not { } buffer || TimeLimitReached)
            return false;

        uint size = PacketSize;
        uint received = Native.GetRawInputData(rawInput, InputData, out var packet, ref size, HeaderSize);
        if (received != PacketSize || packet.Header.Type != 0 || packet.Header.Device == 0 ||
            (packet.Flags & Absolute) != 0)
            return true;

        return buffer.Add((int)Stopwatch.GetElapsedTime(_start).TotalMilliseconds, packet.DeltaX, packet.DeltaY);
    }

    public ImmutableArray<MouseSample> Stop()
    {
        if (_buffer is not { } buffer)
            return [];
        Dispose();
        return buffer.Snapshot();
    }

    public void Dispose()
    {
        if (_buffer is null)
            return;
        _buffer = null;
        var device = new Native.RawInputDevice { UsagePage = GenericDesktop, Usage = Mouse, Flags = Remove };
        Native.RegisterRawInputDevices(in device, 1, DeviceSize);
    }
}
