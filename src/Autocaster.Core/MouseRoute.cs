using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Autocaster.Core;

public readonly record struct MouseSample(int OffsetMs, int DeltaX, int DeltaY)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ControlPoint { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Interpolate { get; init; }
}

public readonly record struct MouseMapPoint(long X, long Y);

public static class MouseRoute
{
    public const int IntervalMs = 8;
    public const int MaxDurationMs = 60_000;
    public const int MaxSamples = MaxDurationMs / IntervalMs;
    public const int MaxDelta = 1_000_000;

    public static int ForwardDurationMs(ScenarioAction action) =>
        action.MouseDurationMs ?? (int)Math.Round(action.MousePath[^1].OffsetMs / action.MouseSpeed);

    public static ImmutableArray<MouseSample> InsertPoint(ImmutableArray<MouseSample> path, int index, double fraction)
    {
        if ((uint)index >= (uint)path.Length || !double.IsFinite(fraction))
            return path;
        var sample = path[index];
        int previous = index == 0 ? 0 : path[index - 1].OffsetMs;
        fraction = Math.Clamp(fraction, 0, 1);
        int x = (int)Math.Round(sample.DeltaX * fraction);
        int y = (int)Math.Round(sample.DeltaY * fraction);
        if ((x == 0 && y == 0) || (x == sample.DeltaX && y == sample.DeltaY) ||
            sample.OffsetMs - previous < 2 || path.Length == MaxSamples)
        {
            // A dense recording can already have a sample at the requested point.
            int nearest = fraction < .5 ? index - 1 : index;
            return nearest >= 0 ? path.SetItem(nearest, path[nearest] with { ControlPoint = true }) : path;
        }

        int offset = previous + (int)Math.Round((sample.OffsetMs - previous) * fraction);
        offset = Math.Clamp(offset, previous + 1, sample.OffsetMs - 1);
        return path.SetItem(index, sample with { DeltaX = sample.DeltaX - x, DeltaY = sample.DeltaY - y })
            .Insert(index, new MouseSample(offset, x, y) { ControlPoint = true, Interpolate = sample.Interpolate });
    }

    public static ImmutableArray<MouseSample> RemovePoint(ImmutableArray<MouseSample> path, int index)
    {
        if (index < 0 || index >= path.Length - 1)
            return path;
        var next = path[index + 1];
        long x = (long)path[index].DeltaX + next.DeltaX;
        long y = (long)path[index].DeltaY + next.DeltaY;
        if (Math.Abs(x) > MaxDelta || Math.Abs(y) > MaxDelta)
            return path;
        return path.SetItem(index + 1, next with { DeltaX = (int)x, DeltaY = (int)y,
            Interpolate = next.Interpolate || path[index].Interpolate }).RemoveAt(index);
    }

    internal static ImmutableArray<MouseSample> Expand(ImmutableArray<MouseSample> path, int playbackMs, bool smooth)
    {
        if (!smooth && !path.Any(sample => sample.Interpolate))
            return path;
        var result = ImmutableArray.CreateBuilder<MouseSample>();
        int step = Math.Max(1, (int)Math.Min(MaxDurationMs,
            Math.Ceiling((double)path[^1].OffsetMs * IntervalMs / Math.Max(1, playbackMs))));
        step = Math.Max(step, (path[^1].OffsetMs + MaxSamples - 1) / MaxSamples);
        int previous = 0;
        foreach (var sample in path)
        {
            int x = 0, y = 0;
            if ((sample.Interpolate || smooth) && (sample.DeltaX != 0 || sample.DeltaY != 0))
                for (int at = previous + step; at < sample.OffsetMs; at += step)
                {
                    double fraction = (double)(at - previous) / (sample.OffsetMs - previous);
                    int nextX = (int)Math.Round(sample.DeltaX * fraction), nextY = (int)Math.Round(sample.DeltaY * fraction);
                    result.Add(new(at, nextX - x, nextY - y));
                    (x, y) = (nextX, nextY);
                }
            result.Add(new(sample.OffsetMs, sample.DeltaX - x, sample.DeltaY - y));
            previous = sample.OffsetMs;
        }
        return result.ToImmutable();
    }

    internal static void Validate(ImmutableArray<MouseSample> samples, Guid actionId)
    {
        if (samples.IsDefault || samples.Length > MaxSamples)
            throw new ScenarioException("Invalid mouse route length.", actionId);

        int previous = 0;
        foreach (var sample in samples)
        {
            if (sample.OffsetMs <= previous || sample.OffsetMs > MaxDurationMs ||
                Math.Abs((long)sample.DeltaX) > MaxDelta || Math.Abs((long)sample.DeltaY) > MaxDelta)
                throw new ScenarioException("The mouse route contains an invalid movement or timestamp.", actionId);
            previous = sample.OffsetMs;
        }
    }

    internal static ImmutableArray<MouseSample> Vary(ImmutableArray<MouseSample> samples, Random random)
    {
        if (samples.Length < 2)
            return samples;

        double distance = samples.Sum(sample => Math.Sqrt((double)sample.DeltaX * sample.DeltaX + (double)sample.DeltaY * sample.DeltaY));
        int amplitude = Math.Clamp((int)Math.Round(distance * 0.015), 1, 12);
        double phase = random.NextDouble() * 2 * Math.PI;
        int previousX = 0, previousY = 0;
        var result = ImmutableArray.CreateBuilder<MouseSample>(samples.Length);
        foreach (var sample in samples)
        {
            double position = (double)sample.OffsetMs / samples[^1].OffsetMs;
            double envelope = position == 1 ? 0 : Math.Sin(Math.PI * position);
            double angle = 2 * Math.PI * position + phase;
            int x = (int)Math.Round(amplitude * envelope * Math.Cos(angle));
            int y = (int)Math.Round(amplitude * envelope * Math.Sin(angle));
            result.Add(new(sample.OffsetMs, sample.DeltaX + x - previousX, sample.DeltaY + y - previousY));
            previousX = x;
            previousY = y;
        }
        return result.MoveToImmutable();
    }
}

public sealed class MouseRouteBuffer
{
    public const int DefaultPointLimit = 20;
    public const int MinPointLimit = 3;
    public const int MaxPointLimit = 250;
    private readonly List<MouseSample> _samples = [];
    private int? _first;

    public bool Add(int elapsedMs, int deltaX, int deltaY)
    {
        if (deltaX == 0 && deltaY == 0)
            return true;
        _first ??= elapsedMs;
        int offset = ((elapsedMs - _first.Value) / MouseRoute.IntervalMs + 1) * MouseRoute.IntervalMs;
        if (offset > MouseRoute.MaxDurationMs)
            return false;

        if (_samples.Count > 0 && _samples[^1].OffsetMs == offset)
        {
            deltaX = Combine(_samples[^1].DeltaX, deltaX);
            deltaY = Combine(_samples[^1].DeltaY, deltaY);
            _samples[^1] = new(offset, deltaX, deltaY);
        }
        else
        {
            _samples.Add(new(offset, Combine(0, deltaX), Combine(0, deltaY)));
        }
        return true;
    }

    public ImmutableArray<MouseSample> Snapshot() => _samples.ToImmutableArray();

    public static ImmutableArray<MouseSample> Compact(ImmutableArray<MouseSample> samples, int pointLimit)
    {
        if (pointLimit is < MinPointLimit or > MaxPointLimit)
            throw new ArgumentOutOfRangeException(nameof(pointLimit));
        // The origin is implicit and counts as one of the visible points.
        int limit = pointLimit - 1;
        if (samples.Length <= limit)
            return samples;

        var points = new List<(int Time, long X, long Y)>();
        long x = 0, y = 0;
        int previousTime = 0;
        foreach (var sample in samples)
        {
            if (sample.OffsetMs - previousTime >= 64)
                points.Add((sample.OffsetMs - MouseRoute.IntervalMs, x, y));
            x += sample.DeltaX;
            y += sample.DeltaY;
            points.Add((sample.OffsetMs, x, y));
            previousTime = sample.OffsetMs;
        }

        int end = points.Count - 1;
        var keep = new SortedSet<int> { 0 };
        var candidates = new PriorityQueue<(int Start, int End, int Split), double>();
        int start = 0;
        for (int i = 1; i < points.Count; i++)
        {
            if (Math.Abs(points[i].X - points[start].X) <= MouseRoute.MaxDelta &&
                Math.Abs(points[i].Y - points[start].Y) <= MouseRoute.MaxDelta)
                continue;
            keep.Add(i - 1);
            QueueSegment(start, i - 1);
            start = i - 1;
        }
        keep.Add(end);
        QueueSegment(start, end);
        if (keep.Count > limit)
            return samples; // An exceptional delta cannot be merged without clipping.
        while (keep.Count < limit && candidates.TryDequeue(out var segment, out _))
        {
            var middle = points[segment.Split];
            var left = points[segment.Start];
            var right = points[segment.End];
            if (!Fits(left, middle) || !Fits(middle, right))
                continue;
            keep.Add(segment.Split);
            QueueSegment(segment.Start, segment.Split);
            QueueSegment(segment.Split, segment.End);
        }

        var result = ImmutableArray.CreateBuilder<MouseSample>(keep.Count);
        int previous = -1;
        (int Time, long X, long Y) origin = default;
        foreach (int index in keep)
        {
            var point = points[index];
            result.Add(new(point.Time, (int)(point.X - origin.X), (int)(point.Y - origin.Y))
                { Interpolate = index > previous + 1 || point.Time - origin.Time <= 16 });
            origin = point;
            previous = index;
        }
        return result.MoveToImmutable();

        static bool Fits((int Time, long X, long Y) a, (int Time, long X, long Y) b) =>
            Math.Abs(b.X - a.X) <= MouseRoute.MaxDelta && Math.Abs(b.Y - a.Y) <= MouseRoute.MaxDelta;

        void QueueSegment(int left, int right)
        {
            var a = points[left];
            var b = points[right];
            double largest = 1;
            int split = -1;
            for (int i = left + 1; i < right; i++)
            {
                var point = points[i];
                double fraction = (double)(point.Time - a.Time) / (b.Time - a.Time);
                double dx = point.X - a.X - (b.X - a.X) * fraction;
                double dy = point.Y - a.Y - (b.Y - a.Y) * fraction;
                double error = dx * dx + dy * dy;
                if (error > largest)
                    (largest, split) = (error, i);
            }
            if (split >= 0)
                candidates.Enqueue((left, right, split), -largest);
        }
    }

    private static int Combine(int first, int second) =>
        (int)Math.Clamp((long)first + second, -MouseRoute.MaxDelta, MouseRoute.MaxDelta);
}
