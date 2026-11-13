using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace Autocaster.Windows;

internal sealed class RathuumScreenFrames : IDisposable
{
    private readonly WindowTargetService _target = new();
    private readonly bool _compact;
    private readonly bool _useGdi;
    private readonly Bitmap _atlas = new(RathuumHudReader.FeedArea.Width,
        RathuumHudReader.FeedArea.Height + RathuumHudReader.ScoreHeight, PixelFormat.Format32bppRgb);
    private Bitmap? _feed;
    private Bitmap? _score;
    private RathuumDesktopDuplication? _duplication;
    private long _tryDuplicationAfter;

    internal string Backend => _duplication is null ? "gdi" : "dxgi";
    internal bool HasTarget { get; private set; }
    internal bool TryGetFocusedClientBounds(out Rectangle client) => _target.TryGetFocusedClientBounds(out client);

    internal RathuumScreenFrames(bool compact, bool useGdi = false, bool setProcessDpi = false)
    {
        _compact = compact;
        _useGdi = useGdi;
        if (setProcessDpi)
            SetProcessDpiAwarenessContext(-4);
    }

    internal bool TryRead(byte[] pixels, out long presented)
    {
        presented = 0;
        HasTarget = _target.TryGetFocusedClientBounds(out var client) && client.Width >= 640 && client.Height >= 360;
        if (!HasTarget)
        {
            ResetDuplication();
            return false;
        }
        try
        {
            var feedArea = RathuumHudReader.FeedArea;
            var feed = Scale(feedArea, client);
            var score = Scale(RathuumHudReader.ScoreArea(_compact), client);
            if (_duplication is not null && !_duplication.Contains(client))
                ResetDuplication();
            if (!_useGdi && _duplication is null && Stopwatch.GetTimestamp() >= _tryDuplicationAfter)
            {
                _tryDuplicationAfter = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
                _duplication = RathuumDesktopDuplication.Open(client);
            }
            using (var canvas = Graphics.FromImage(_atlas))
            {
                canvas.InterpolationMode = InterpolationMode.Bilinear;
                if (_duplication is not null)
                {
                    if (!_duplication.TryRead(canvas, feed, score, out presented))
                        return false;
                }
                else
                {
                    PaintRegion(canvas, ref _feed, feed, new(Point.Empty, feedArea.Size));
                    PaintRegion(canvas, ref _score, score,
                        new(0, feedArea.Height, RathuumHudReader.ScoreWidth, RathuumHudReader.ScoreHeight));
                    presented = Stopwatch.GetTimestamp();
                }
            }
            if (!_target.TryGetFocusedClientBounds(out var current) || current != client)
                return false;

            var bits = _atlas.LockBits(new(Point.Empty, _atlas.Size), ImageLockMode.ReadOnly, _atlas.PixelFormat);
            try
            {
                int rowBytes = _atlas.Width * 4;
                for (int row = 0; row < _atlas.Height; row++)
                    Marshal.Copy(bits.Scan0 + row * bits.Stride, pixels, row * rowBytes, rowBytes);
            }
            finally
            {
                _atlas.UnlockBits(bits);
            }
            return true;
        }
        catch (Exception error) when (error is ExternalException or Win32Exception or SharpGenException)
        {
            ResetDuplication();
            _tryDuplicationAfter = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            return false;
        }
    }

    private static void PaintRegion(Graphics canvas, ref Bitmap? image, Rectangle source, Rectangle destination)
    {
        if (image is null || image.Size != source.Size)
        {
            image?.Dispose();
            image = new(source.Width, source.Height, PixelFormat.Format32bppRgb);
        }
        using (var capture = Graphics.FromImage(image))
            capture.CopyFromScreen(source.Location, Point.Empty, image.Size);
        canvas.DrawImage(image, destination, new(Point.Empty, image.Size), GraphicsUnit.Pixel);
    }

    private static Rectangle Scale(Rectangle region, Rectangle client) => Rectangle.FromLTRB(
        client.Left + region.Left * client.Width / 1920, client.Top + region.Top * client.Height / 1080,
        client.Left + region.Right * client.Width / 1920, client.Top + region.Bottom * client.Height / 1080);

    public void Dispose()
    {
        ResetDuplication();
        _atlas.Dispose();
        _feed?.Dispose();
        _score?.Dispose();
    }

    private void ResetDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint context);
}
