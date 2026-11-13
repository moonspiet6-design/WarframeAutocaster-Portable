namespace Autocaster.Windows;

public static class LarvaHudDetector
{
    // Reference client crop at 1920 × 1080. It includes the countdown over ability slot 2 only.
    public const int RightInset = 293;
    public const int BottomInset = 106;
    public const int Width = 38;
    public const int Height = 20;

    public static bool HasCountdown(ReadOnlySpan<int> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > pixels.Length)
            return false;
        int whiteFloor = width == Width && height == Height ? 165 : 140;
        Span<bool> white = stackalloc bool[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            int sourceY = (2 * y + 1) * height / (2 * Height);
            for (int x = 0; x < Width; x++)
            {
                int sourceX = (2 * x + 1) * width / (2 * Width);
                int pixel = pixels[sourceY * width + sourceX];
                int r = (pixel >> 16) & 255, g = (pixel >> 8) & 255, b = pixel & 255;
                int low = Math.Min(r, Math.Min(g, b));
                int high = Math.Max(r, Math.Max(g, b));
                white[y * Width + x] = low >= whiteFloor && high - low <= 48;
            }
        }

        // Require two full-height glyphs and a small decimal point aligned to their baseline.
        // The empty space above the point rejects solid highlights and bright background edges.
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int dot = Count(white, 15 + dx, 14 + dy, 4, 4);
            if (dot is >= 2 and <= 10 && Count(white, 15 + dx, 2 + dy, 4, 12) <= 4 &&
                LooksLikeDigit(white, 2 + dx, 2 + dy) && LooksLikeDigit(white, 20 + dx, 2 + dy))
                return true;
        }
        return false;
    }

    private static bool LooksLikeDigit(ReadOnlySpan<bool> white, int left, int top)
    {
        int filled = 0, rows = 0, widest = 0, cap = 0, baseline = 0;
        for (int y = 0; y < 16; y++)
        {
            int row = Count(white, left, top + y, 13, 1);
            filled += row;
            if (row > 0)
                rows++;
            widest = Math.Max(widest, row);
            if (y < 3)
                cap += row;
            if (y >= 13)
                baseline += row;
        }
        return filled is >= 21 and <= 128 && rows >= 11 && widest >= 3 && cap >= 3 && baseline >= 3;
    }

    private static int Count(ReadOnlySpan<bool> white, int left, int top, int width, int height)
    {
        int count = 0;
        for (int y = top; y < top + height; y++)
        for (int x = left; x < left + width; x++)
            if (white[y * Width + x])
                count++;
        return count;
    }
}
