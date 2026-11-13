namespace Autocaster.Core;

public sealed class RathuumScoreCache
{
    private static readonly TimeSpan ConfirmationGap = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(350);
    private int? _score;
    private int _reads;
    private TimeSpan _lastSeen;

    public void Observe(TimeSpan timestamp, int? score)
    {
        if (score is not (>= 0 and < 25))
        {
            if (score.HasValue)
                Clear();
            return;
        }
        if (_score != score || timestamp - _lastSeen > ConfirmationGap)
        {
            _score = score;
            _reads = 0;
        }
        else if (timestamp <= _lastSeen)
            return;
        _lastSeen = timestamp;
        _reads = Math.Min(3, _reads + 1);
    }

    public int? Read(TimeSpan now) =>
        _reads >= (_score == 0 ? 2 : 3) && now >= _lastSeen && now - _lastSeen <= MaxAge ? _score : null;

    public void Clear()
    {
        _score = null;
        _reads = 0;
    }
}
