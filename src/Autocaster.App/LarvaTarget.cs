using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Autocaster.Core;
using Autocaster.Windows;

namespace Autocaster.App;

internal class LarvaTarget(Func<int>? readWaveSize = null, RathuumLiveMonitor? warmup = null) : WindowTargetService, ILarvaTarget, IRathuumTarget
{
    public IRathuumMonitor StartRathuumMonitor(RathuumGateOptions options) =>
        warmup?.TryLease(options) ?? new RathuumLiveMonitor(options, readWaveSize);

    public LarvaHudState ReadLarvaHud()
    {
        if (!TryGetClientArea(out int x, out int y, out int width, out int height) || width < 640 || height < 360)
            return LarvaHudState.Unavailable;

        int right = width - Scale(LarvaHudDetector.RightInset - LarvaHudDetector.Width, width, 1920);
        int left = width - Scale(LarvaHudDetector.RightInset, width, 1920);
        int bottom = height - Scale(LarvaHudDetector.BottomInset - LarvaHudDetector.Height, height, 1080);
        int top = height - Scale(LarvaHudDetector.BottomInset, height, 1080);
        try
        {
            using var crop = new Bitmap(right - left, bottom - top, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(crop))
                graphics.CopyFromScreen(x + left, y + top, 0, 0, crop.Size);
            var bits = crop.LockBits(new Rectangle(Point.Empty, crop.Size), ImageLockMode.ReadOnly, crop.PixelFormat);
            try
            {
                var pixels = new int[crop.Width * crop.Height];
                for (int row = 0; row < crop.Height; row++)
                    Marshal.Copy(bits.Scan0 + row * bits.Stride, pixels, row * crop.Width, crop.Width);
                return LarvaHudDetector.HasCountdown(pixels, crop.Width, crop.Height)
                    ? LarvaHudState.Countdown : LarvaHudState.Idle;
            }
            finally { crop.UnlockBits(bits); }
        }
        catch (Exception error) when (error is Win32Exception or ExternalException)
        {
            return LarvaHudState.Unavailable;
        }
    }

    private static int Scale(int value, int length, int reference) => (value * length + reference / 2) / reference;
}
