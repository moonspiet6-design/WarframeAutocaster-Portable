using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Autocaster.Windows;

internal sealed class RathuumScoreReader(OcrEngine ocr, bool compact) : IDisposable
{
    private const int Width = RathuumHudReader.ScoreWidth, Height = RathuumHudReader.ScoreHeight;
    private const int TrackingHeight = 104;
    private static readonly Regex ScorePattern = new(@"\bAccused\s*[:;.]?\s*([0-9oil])(?:\s?([0-9oil]))?\b", RegexOptions.IgnoreCase);
    private readonly SoftwareBitmap _full = new(BitmapPixelFormat.Bgra8, Width, Height);
    private readonly SoftwareBitmap _tracked = new(BitmapPixelFormat.Bgra8, Width, TrackingHeight);
    private readonly SoftwareBitmap _contrast = new(BitmapPixelFormat.Gray8, Width, TrackingHeight);
    private readonly byte[] _fullPixels = new byte[Width * Height * 4];
    private readonly byte[] _trackedPixels = new byte[Width * TrackingHeight * 4];
    private readonly byte[] _contrastPixels = new byte[Width * TrackingHeight];
    private int? _rowCenter;
    private TimeSpan? _lastSearch;

    internal async Task<int> ReadAsync(byte[] atlas, TimeSpan timestamp, int scoreFloor, bool bright)
    {
        int trackedRead = -1;
        if (_rowCenter is int center)
        {
            int top = Math.Clamp(center - TrackingHeight / 2, 0, Height - TrackingHeight);
            trackedRead = await ReadRegion(atlas, _tracked, _trackedPixels, top);
            if (trackedRead >= 0 && trackedRead >= scoreFloor)
            {
                _lastSearch = null;
                return trackedRead;
            }
        }

        // Once the label leaves the tracked strip, search the wider area at a bounded rate.
        if (_lastSearch is TimeSpan last && timestamp - last < TimeSpan.FromMilliseconds(35))
            return trackedRead;
        _lastSearch = timestamp;
        int fullRead = await ReadRegion(atlas, _full, _fullPixels, 0);
        if ((fullRead >= 0 && fullRead >= scoreFloor) || compact || _rowCenter is not int lastCenter)
            return fullRead >= 0 ? fullRead : trackedRead;

        // Cyan damage and bright effects can interfere with the raw text pass.
        int contrastTop = Math.Clamp(lastCenter - TrackingHeight / 2, 0, Height - TrackingHeight);
        CopyContrast(atlas, contrastTop, bright);
        _contrast.CopyFromBuffer(_contrastPixels.AsBuffer());
        int contrastRead = await Recognize(_contrast, contrastTop);
        if (contrastRead >= 0 && contrastRead >= scoreFloor)
            return contrastRead;
        return fullRead >= 0 ? fullRead : trackedRead >= 0 ? trackedRead : contrastRead;
    }

    private async Task<int> ReadRegion(byte[] atlas, SoftwareBitmap image, byte[] pixels, int top)
    {
        int atlasWidth = RathuumHudReader.FeedArea.Width;
        int atlasTop = RathuumHudReader.FeedArea.Height + top;
        for (int row = 0; row < image.PixelHeight; row++)
            Array.Copy(atlas, (atlasTop + row) * atlasWidth * 4, pixels, row * Width * 4, Width * 4);
        image.CopyFromBuffer(pixels.AsBuffer());
        return await Recognize(image, top);
    }

    private async Task<int> Recognize(SoftwareBitmap image, int top)
    {
        var result = await ocr.RecognizeAsync(image);
        foreach (var line in result.Lines)
        {
            var parsed = ScorePattern.Match(line.Text);
            if (!parsed.Success || line.Words.Count == 0)
                continue;
            int score = Digit(parsed.Groups[1].Value[0]);
            if (parsed.Groups[2].Success)
            {
                if (score == 0)
                    continue;
                score = score * 10 + Digit(parsed.Groups[2].Value[0]);
            }
            if (score > 25)
                continue;
            var bounds = line.Words[0].BoundingRect;
            _rowCenter = top + (int)Math.Round(bounds.Top + bounds.Height / 2);
            return score;
        }
        return -1;
    }

    private static int Digit(char character) => character switch
    {
        'o' or 'O' => 0,
        'i' or 'I' or 'l' or 'L' => 1,
        _ => character - '0'
    };

    private void CopyContrast(byte[] atlas, int top, bool bright)
    {
        int atlasWidth = RathuumHudReader.FeedArea.Width;
        int atlasTop = RathuumHudReader.FeedArea.Height + top;
        for (int row = 0; row < TrackingHeight; row++)
        {
            int pixel = (atlasTop + row) * atlasWidth * 4;
            for (int col = 0; col < Width; col++, pixel += 4)
            {
                int blue = atlas[pixel], green = atlas[pixel + 1], red = atlas[pixel + 2];
                bool faded = bright && blue >= 125;
                _contrastPixels[row * Width + col] = red >= 100 && red - blue >= (faded ? 17 : 40) &&
                    green - blue >= (faded ? 7 : 14) && red >= green ? (byte)0 : (byte)255;
            }
        }
    }

    public void Dispose()
    {
        _contrast.Dispose();
        _tracked.Dispose();
        _full.Dispose();
    }
}
