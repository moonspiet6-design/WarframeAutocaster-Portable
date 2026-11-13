using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Autocaster.Windows;

internal sealed class RathuumGameFps : IDisposable
{
    private static readonly Regex FrameRate = new(@"\bRate\s*:?\s*([0-9Il][0-9Il\s]{1,6})\s*fps\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly OcrEngine _ocr = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
        ?? throw new InvalidOperationException("English OCR is unavailable in Windows.");
    private readonly Bitmap _sample = new(400, 60, PixelFormat.Format32bppRgb);
    private readonly byte[] _pixels = new byte[400 * 60 * 4];
    private readonly SoftwareBitmap _image = new(BitmapPixelFormat.Bgra8, 400, 60);
    private int _consecutiveFast;

    internal int ScanCap { get; private set; } = 60;

    internal async Task<int?> ReadAsync(RathuumScreenFrames capture)
    {
        if (!capture.TryGetFocusedClientBounds(out var client))
            return null;
        int height = Math.Max(1, client.Height * 30 / 1080);
        int width = Math.Max(1, client.Width * 200 / 1920);
        try
        {
            using var source = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using (var screen = Graphics.FromImage(source))
                screen.CopyFromScreen(client.Left, client.Bottom - height, 0, 0, source.Size);
            if (!capture.TryGetFocusedClientBounds(out var current) || current != client)
                return null;
            using (var graphics = Graphics.FromImage(_sample))
                graphics.DrawImage(source, new Rectangle(Point.Empty, _sample.Size));
            var bits = _sample.LockBits(new(Point.Empty, _sample.Size), ImageLockMode.ReadOnly, _sample.PixelFormat);
            try
            {
                for (int row = 0; row < _sample.Height; row++)
                    Marshal.Copy(bits.Scan0 + row * bits.Stride, _pixels, row * _sample.Width * 4, _sample.Width * 4);
            }
            finally { _sample.UnlockBits(bits); }
            _image.CopyFromBuffer(_pixels.AsBuffer());
            var result = await _ocr.RecognizeAsync(_image);
            return Parse(result.Text);
        }
        catch (Exception error) when (error is ExternalException or Win32Exception)
        {
            return null;
        }
    }

    private static int? Parse(string text)
    {
        var match = FrameRate.Match(text);
        if (!match.Success)
            return null;
        string digits = string.Concat(match.Groups[1].Value.Where(c => !char.IsWhiteSpace(c))
            .Select(c => char.IsAsciiDigit(c) ? c : '1'));
        return digits.Length is >= 2 and <= 4 && int.TryParse(digits, out int fps) && fps is >= 10 and <= 1000
            ? fps : null;
    }

    internal void Observe(int? fps, int requested)
    {
        if (fps >= (ScanCap < 60 ? 240 : 255))
        {
            if (++_consecutiveFast == 3)
            {
                ScanCap = ScanCap < 60 ? Math.Min(60, requested) : requested;
                _consecutiveFast = 0;
            }
            return;
        }

        _consecutiveFast = 0;
        if (fps < 240)
            ScanCap = 30;
        else
            ScanCap = Math.Min(ScanCap, 60);
    }

    public void Dispose()
    {
        _sample.Dispose();
        _image.Dispose();
    }
}
