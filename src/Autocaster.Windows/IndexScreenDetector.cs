using System.Buffers.Binary;
using Autocaster.Core;

namespace Autocaster.Windows;

public readonly record struct IndexVoteLayout(double Scale, int OffsetY)
{
    public int X(int x) => (int)Math.Round(960 + (x - 960) * Scale);
    public int Y(int y) => (int)Math.Round(y * Scale) + OffsetY;
}

public readonly record struct IndexFrame(IndexScreen Screen, IndexVoteLayout VoteLayout = default);

// The templates sample the vote and results UI independently of the arena.
public static class IndexScreenDetector
{
    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;

    private static readonly IndexVoteLayout Reference = new(1, 0);
    private static readonly VoteProbe[] VoteLayouts = BuildLayouts();

    public static IndexScreen Classify(Func<int, int, int> pixel) => Inspect(pixel).Screen;

    // Pixel samples are packed as 0xRRGGBB at reference coordinates.
    public static IndexFrame Inspect(Func<int, int, int> pixel, IndexVoteLayout? lastVote = null)
    {
        if (Matches(Victory, pixel, Reference) && Matches(ResultsCaption, pixel, Reference))
            return new(IndexScreen.Results);
        var previous = lastVote is null ? null : Array.Find(VoteLayouts, probe => probe.Layout == lastVote);
        if (previous is not null && IsVote(pixel, previous))
            return new(IndexScreen.Vote, previous.Layout);
        foreach (var probe in VoteLayouts)
        {
            if (probe != previous && IsVote(pixel, probe))
                return new(IndexScreen.Vote, probe.Layout);
        }
        return new(IndexScreen.NoVote);
    }

    private static VoteProbe[] BuildLayouts()
    {
        var scales = Enumerable.Range(30, 21).OrderBy(value => Math.Abs(value - 40)).Select(value => value / 40d);
        int[] shifts = [0, -10, 10, -20, 20, -30, 30, -40, 40];
        int[] anchors = [540, 0];
        // Choice cards are centered horizontally. Test both the centered and top-anchored vertical layouts.
        return (from scale in scales
            from shift in shifts
            from anchor in anchors
            select new IndexVoteLayout(scale, (int)Math.Round(anchor * (1 - scale)) + shift))
            .Distinct().Select(layout => new VoteProbe(layout)).ToArray();
    }

    private sealed class VoteProbe(IndexVoteLayout layout)
    {
        internal IndexVoteLayout Layout { get; } = layout;
        internal int[] LeftX { get; } = Enumerable.Range(0, 14).Select(col => layout.X(272 + col * 41)).ToArray();
        internal int[] RightX { get; } = Enumerable.Range(0, 14).Select(col => layout.X(1088 + col * 41)).ToArray();
        internal int[] Rows { get; } = Enumerable.Range(0, 10).Select(row => layout.Y(290 + row * 43)).ToArray();
    }

    private static bool IsVote(Func<int, int, int> pixel, VoteProbe probe)
    {
        int left = CountCardPatches(probe.LeftX, probe.Rows, Left0, Left1, pixel);
        int right = CountCardPatches(probe.RightX, probe.Rows, Right0, Right1, pixel);
        // Both distinct pieces of artwork can identify the screen even when the labels are obscured or recolored.
        if (left >= 2 && right >= 2 && left + right >= 6)
            return true;
        // One partially obscured card still needs independent evidence from the choice labels.
        return left + right >= 3 && (Matches(CashIn, pixel, probe.Layout) || Matches(Stay, pixel, probe.Layout));
    }

    private static bool Matches(Mask mask, Func<int, int, int> pixel, IndexVoteLayout layout)
    {
        // A two-pixel search accommodates integer scaling and slightly different text rasterization.
        for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
        {
            if (MatchesAt(mask, pixel, layout, dx, dy))
                return true;
        }
        return false;
    }

    private static bool MatchesAt(Mask mask, Func<int, int, int> pixel, IndexVoteLayout layout, int dx, int dy)
    {
        int positives = 0;
        foreach (ushort point in mask.Lit)
        {
            if (IsTone(pixel(layout.X(mask.X + point % mask.Width) + dx, layout.Y(mask.Y + point / mask.Width) + dy), mask.Gold))
                positives++;
        }
        if (positives < (mask.Gold ? 40 : 33))
            return false;
        int negatives = 0;
        foreach (ushort point in mask.Dark)
        {
            if (!IsTone(pixel(layout.X(mask.X + point % mask.Width) + dx, layout.Y(mask.Y + point / mask.Width) + dy), mask.Gold))
                negatives++;
        }
        return negatives >= (mask.Gold ? 33 : 30);
    }

    private static bool IsTone(int rgb, bool gold)
    {
        int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        return gold
            ? r >= 105 && r - g >= 7 && g - b >= 19 && r - b >= 32
            : Math.Min(r, Math.Min(g, b)) >= 155 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 50;
    }

    private static int CountCardPatches(int[] columns, int[] rows, byte[] neutral, byte[] highlighted, Func<int, int, int> pixel)
    {
        Span<byte> current = stackalloc byte[140];
        for (int row = 0; row < 10; row++)
        for (int col = 0; col < 14; col++)
        {
            int rgb = pixel(columns[col], rows[row]);
            current[row * 14 + col] = (byte)((((rgb >> 16) & 255) * 3 + ((rgb >> 8) & 255) * 6 + (rgb & 255)) / 10);
        }

        int matches = 0;
        for (int row = 0; row < 10; row += 5)
        for (int col = 0; col < 14; col += 7)
        {
            if (MatchesPatch(row, col, current, neutral) || MatchesPatch(row, col, current, highlighted))
                matches++;
        }
        return matches;
    }

    private static bool MatchesPatch(int row, int col, ReadOnlySpan<byte> current, byte[] expected)
    {
        int sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
        const int count = 35;
        for (int y = row; y < row + 5; y++)
        for (int x = col; x < col + 7; x++)
        {
            int index = y * 14 + x;
            int a = current[index], b = expected[index];
            sumA += a;
            sumB += b;
            sumAA += a * a;
            sumBB += b * b;
            sumAB += a * b;
        }
        double covariance = count * sumAB - sumA * sumB;
        int varianceA = count * sumAA - sumA * sumA;
        double variance = (double)varianceA * (count * sumBB - sumB * sumB);
        return varianceA >= count * count * 36 && covariance > 0 && covariance * covariance >= 0.82 * variance;
    }

    private sealed record Mask(int X, int Y, int Width, bool Gold, string LitData, string DarkData)
    {
        internal ushort[] Lit { get; } = Decode(LitData);
        internal ushort[] Dark { get; } = Decode(DarkData);

        private static ushort[] Decode(string data)
        {
            var bytes = Convert.FromBase64String(data);
            var result = new ushort[bytes.Length / sizeof(ushort)];
            for (int i = 0; i < result.Length; i++)
                result[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort)));
            return result;
        }
    }


    private static readonly Mask CashIn = new(695, 166, 173, true,
        "ogNPBMcEFwWOBQIGVwbLBhgHfQfLBysIoQjaCFAJmQn8CUcKqgr2CmgLpQsVDFIMsAz8DDQNpw3hDVQOjg4ADzsPrQ8REFwQ0RAPEYER1REyEoMS9BIvE6ET2hM4FIMU4BQdFYoVyhUnFogW7BaYFw==",
        "7ABqAeYBZALiAmUDCQTBBIgFTAYjB+EHpQhsCSgK5wqdC14MGw3RDZYOTw8HEMcQkxFwEkQTDBTTFKQVfhZCF+8XhBgCGX8Z/Rl7GvcadRs=");
    private static readonly Mask Stay = new(1058, 166, 320, true,
        "DgeIB08IxghZCdYJPwroCn8LKQzbDK4NXw4jD8oPlRBAEQUSrxJZE/cTnhRiFfEVohYyF8kXcBj1GJsZGhrZGm4bGxy1HF8dIh61HoEfKCDYIJghRiLuIpsjNyTzJKMlZCYYJ9knnygzKQwqiSq1Kw==",
        "uAGqApoDjAR+BYoG7wd8CQgLfgzpDUUPpRD3EVETwBQYFnQX0BhDGqcbBB1aHqYf9iBPIrgjEyVyJswnPCmmKvQrHy0PLgEv8y/lMNUxxzI=");
    private static readonly Mask Victory = new(830, 240, 260, true,
        "PQexB28I1Ah3CdgJcQrACjoLnQv+C7kMUw33DagOXg8SENsQeRE4EgcTuxOCFGAVKBb3FqEXchg2GQEahRomG/UbihwsHQ8elh5QHycg8CDHIbsipiOhJGklQyYzJwoo6CjVKX0qOiv7K8gsiS3NLg==",
        "ggGAAn0DewR4BXYG8AegCaULYg39DpIQHhKsEykVqhYmGLMZSxvLHF0e3B9TIb8iIiSJJfcmZijHKVArvCwcLlkvaDBlMWMyYDNeNFs1WTY=");
    private static readonly Mask ResultsCaption = new(602, 998, 320, false,
        "tAIYA44D5wMwBIIE3gQmBWgFvgUABmIG2QYyB6EHEghzCOQIUwmrCSMKlArsCmELqwsDDFoMpAwCDU0Nng3pDUcOkw7iDkEPrQ8LEHsQ4BAzEaIRFxJhEtESQhOaE/UTThSQFNsUJBV8FcUVFRZsFg==",
        "awHBARYCbwIxA/gD/QQoBvMGzweaCHEJQgoPCwsMCw0nDi4PBBDHEK4RkBJTE3oUqxWRFuYWPBeVF+0XQhiXGPIYSRmfGfQZUBqlGvoaVBs=");
    private static readonly byte[] Left0 = Convert.FromBase64String("CQkJCgoKCxAVFg4MMwsLDQ0yEA4PERIUIhgVHRiIFRQbRRMeFh9gHiAgGBsaGhsbOywaGx0lMjcVGV+bIR8gJykvMjhJXhk8U3dEmMW90MViSVhzFVxwkY8LGR7LLSw0dkYSChaFEYyyhR8eHWJI0QsODjR2eZmryP0VHDwcCQoLCgwMrai4vxYaFRY=");
    private static readonly byte[] Left1 = Convert.FromBase64String("CgoKCwsLCxEZGBANNgsMDg0xEA8QERMUJxoXIRmJFxYeRRMfGCJoIiUkGh0cHh4cPi4dHyArOT4YHWGdJSMkLS81Oj9TYR0/V3lGm8e9z8hvaGB0FmF0kZALGCLMMTM9eEoSCxmFE46zhyMfIGxK1AwPDzV4d52uyPwYH0YfCgsLCwoMr6q6wRQbFRw=");
    private static readonly byte[] Right0 = Convert.FromBase64String("FBQWEA8SEhYVExQTDg8lJx4XTUkWEhsdOh8UEUE1Nkg9ORccIyAaISAahTI3lhcdGiIqXSJSKSB4c2WSTQ8eJzEwGhcjKXl1iGMyGRgLMAxFICEgdW5ETGVKHRgXHxwZMBlXg0I0ExciViIYdxgXFFNLSScaEAxwIBcJEhUTM0U6JRlKKxkPDgsUExM=");
    private static readonly byte[] Right1 = Convert.FromBase64String("JyksIh4kJ22NKCc0Hx5LUDwvLFcpLyM3LGE7LIZrcjRIcIkmISY0PzY0wbtyYlmohCsOEyhIREArKSgfamE5T2FYEU4tQCsoHx2RV0FdLEJHNCwx6OBzcl63PRUmNjYUDDCw2YRyJyMqJy0JMDAnJIGVJU4vIhoeHzkKdAUTaCF7TTUvJR0dFJhjKAs=");
}
