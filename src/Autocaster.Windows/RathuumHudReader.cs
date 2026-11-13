using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices.WindowsRuntime;
using Autocaster.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Autocaster.Windows;

internal readonly record struct RathuumReadTiming(double? FeedOcrMs, double ScoreOcrMs, double ProcessingMs);

internal readonly record struct RathuumHudObservation(TimeSpan Timestamp, int? RawScore,
    IReadOnlyList<RathuumFeedEvent> Events, IReadOnlyList<string> VisibleExecutioners, bool FeedUsable);

internal sealed class RathuumHudReader : IDisposable
{
    internal static readonly Rectangle FeedArea = new(620, 554, 680, 520);
    internal const int ScoreWidth = 496, ScoreHeight = 320;
    internal static Rectangle ScoreArea(bool compact) => new(12, compact ? 202 : 270, ScoreWidth / 2, ScoreHeight / 2);
    private readonly OcrEngine _ocr = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
        ?? throw new InvalidOperationException("English OCR is unavailable in Windows.");
    private RathuumKillCounter _counter = new();
    private readonly SoftwareBitmap _feedImage = new(BitmapPixelFormat.Gray8, FeedArea.Width, FeedArea.Height);
    private readonly RathuumScoreReader _score;
    private readonly byte[] _feed = new byte[FeedArea.Width * FeedArea.Height];
    private readonly byte[] _lastOcrFeed = new byte[FeedArea.Width * FeedArea.Height];
    private string[] _lastOcrLines = [];
    private string[] _lastVisibleNames = [];
    private bool _lastFeedHadUnreadableName;
    private bool _hasOcrFeed;
    private readonly Action<int, long, string>? _reportEvent;
    private readonly Action? _killTone;
    private readonly Action<RathuumHudObservation>? _observe;
    private readonly List<double> _feedDurations = [];
    private readonly List<double> _frameDurations = [];
    private int _teamScore = -1;
    private int _scoreTrackingFloor = -1;
    private int _previousRead = -1;
    private TimeSpan _scoreFirstSeen;
    private TimeSpan _lastValidScore;

    internal byte[] Pixels { get; } = new byte[FeedArea.Width * (FeedArea.Height + ScoreHeight) * 4];
    internal int Frames { get; private set; }

    internal RathuumHudReader(bool compact, Action<int, long, string>? reportEvent = null, Action? killTone = null,
        Action<RathuumHudObservation>? observe = null)
    {
        _reportEvent = reportEvent;
        _killTone = killTone;
        _observe = observe;
        _score = new(_ocr, compact);
    }

    internal async Task<int?> ReadScoreOnlyAsync(TimeSpan timestamp)
    {
        int score = await _score.ReadAsync(Pixels, timestamp, -1, false).ConfigureAwait(false);
        return score is >= 0 and <= 25 ? score : null;
    }

    internal void ResetTracking()
    {
        _counter = new();
        _hasOcrFeed = false;
        _previousRead = _teamScore = _scoreTrackingFloor = -1;
    }

    internal async Task<RathuumReadTiming> ReadAsync(TimeSpan timestamp)
    {
        Frames++;
        long began = Stopwatch.GetTimestamp();
        double? feedOcrMs = null;
        IReadOnlyList<RathuumFeedEvent> hits = [];
        string[] visible = [];
        var (cyanPixels, whitePixels, blackPixels) = FilterFeed();
        bool feedUsable = whitePixels < _feed.Length * .65 && blackPixels < _feed.Length * .90;
        if (cyanPixels >= 140)
        {
            if (!_hasOcrFeed || !_feed.AsSpan().SequenceEqual(_lastOcrFeed))
            {
                _feedImage.CopyFromBuffer(_feed.AsBuffer());
                long feedStarted = Stopwatch.GetTimestamp();
                var result = await _ocr.RecognizeAsync(_feedImage);
                feedOcrMs = Stopwatch.GetElapsedTime(feedStarted).TotalMilliseconds;
                _lastOcrLines = result.Lines.Where(IsVictimLine).Select(line => line.Text).ToArray();
                if (_observe is not null)
                {
                    _lastVisibleNames = _lastOcrLines.Select(RathuumKillCounter.ReadExecutionerName).OfType<string>().ToArray();
                    _lastFeedHadUnreadableName = _lastOcrLines.Count(RathuumKillCounter.LooksLikeExecutionerLine) > _lastVisibleNames.Length;
                }
                _feed.CopyTo(_lastOcrFeed, 0);
                _hasOcrFeed = true;
                if (_reportEvent is not null)
                    _feedDurations.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            }
            hits = _counter.Observe(timestamp, _lastOcrLines);
            visible = _lastVisibleNames;
            feedUsable &= !_lastFeedHadUnreadableName;
            foreach (var hit in hits)
                WriteEvent(hit.Kind.ToString(), hit.Name, hit.FirstSeen, hit.Confirmed, hit.ExecutionerCount);
            if (_killTone is not null && hits.Any(hit => hit.Kind == RathuumFeedEventKind.Executioner))
                _killTone();
        }
        else
            feedUsable &= cyanPixels < 40;

        long scoreStarted = Stopwatch.GetTimestamp();
        int read = await _score.ReadAsync(Pixels, timestamp, _scoreTrackingFloor, whitePixels >= _feed.Length / 4);
        double scoreOcrMs = Stopwatch.GetElapsedTime(scoreStarted).TotalMilliseconds;
        if (read >= 0)
        {
            bool repeated = read == _previousRead && timestamp - _lastValidScore <= TimeSpan.FromMilliseconds(80);
            if (!repeated)
                _scoreFirstSeen = timestamp;
            else
            {
                if (read > _scoreTrackingFloor || read == 0 && timestamp - _scoreFirstSeen >= TimeSpan.FromMilliseconds(150))
                    _scoreTrackingFloor = read;
                if (read > _teamScore)
                {
                    _teamScore = read;
                    WriteEvent("Score", "", _scoreFirstSeen, timestamp, _counter.Count);
                }
            }
            _lastValidScore = timestamp;
            _previousRead = read;
        }
        _observe?.Invoke(new(timestamp, read is >= 0 and <= 25 ? read : null, hits, visible, feedUsable));
        double processingMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        if (_reportEvent is not null)
            _frameDurations.Add(processingMs);
        return new(feedOcrMs, scoreOcrMs, processingMs);
    }

    private void WriteEvent(string kind, string name, TimeSpan firstSeen, TimeSpan timestamp, int count)
    {
        if (_reportEvent is null)
            return;
        long decided = Stopwatch.GetTimestamp();
        string row = $"{kind},{name},{firstSeen.TotalSeconds:F6},{timestamp.TotalSeconds:F6},{count},{ScoreText()}";
        _reportEvent(Frames, decided, row);
    }

    internal void WriteSummary()
    {
        _feedDurations.Sort();
        _frameDurations.Sort();
        Console.Error.WriteLine($"Frames {Frames}, feed OCR scans {_feedDurations.Count}, " +
            $"feed p50 {Percentile(_feedDurations, .5):F2} ms, p95 {Percentile(_feedDurations, .95):F2} ms, " +
            $"full processing p95 {Percentile(_frameDurations, .95):F2} ms. " +
            $"Confirmed named executioners {_counter.Count}, HUD score {(_teamScore < 0 ? "unread" : ScoreText())}.");
    }

    private (int Cyan, int White, int Black) FilterFeed()
    {
        int count = 0, white = 0, black = 0;
        for (int i = 0, p = 0; i < _feed.Length; i++, p += 4)
        {
            int blue = Pixels[p], green = Pixels[p + 1], red = Pixels[p + 2];
            bool cyan = green >= 65 && green - red >= 22 && blue - red >= 24 &&
                blue >= green - 28 && blue <= green + 65;
            _feed[i] = cyan ? (byte)0 : (byte)255;
            if (cyan)
                count++;
            if (blue >= 230 && green >= 230 && red >= 230)
                white++;
            if ((blue | green | red) < 8)
                black++;
        }
        return (count, white, black);
    }

    private static bool IsVictimLine(OcrLine line)
    {
        // The complete notification is centered. The cyan name belongs to the victim on the right.
        if (line.Words.Count == 0)
            return false;
        double left = line.Words[0].BoundingRect.Left;
        double right = line.Words[^1].BoundingRect.Right;
        return (left + right) / 2 > 960 - FeedArea.Left;
    }

    private static double Percentile(List<double> values, double fraction) =>
        values.Count == 0 ? 0 : values[(int)((values.Count - 1) * fraction)];

    private string ScoreText() => _teamScore < 0 ? "" : _teamScore.ToString();

    public void Dispose()
    {
        _feedImage.Dispose();
        _score.Dispose();
    }
}
