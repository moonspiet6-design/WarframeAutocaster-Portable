namespace Autocaster.Core;

public enum IndexHudPhase { Preparing, Running, Paused, Vote, StayInvested, CashIn }

public sealed record IndexRunHud(string Name, long Started, int? CashInAfterMs, int CompletedCasts, int PlannedCasts,
    string? Action, long? OperatorDueMs, bool InOperator, long? RotationFrozenAtMs, IndexHudPhase Phase);

internal sealed class IndexHudTimeline
{
    private readonly record struct Rotation(long Marker, int Count);
    private readonly Dictionary<int, Rotation> _rotations = [];
    private ScheduledInput[] _transfers = [];
    private int _nextTransfer;
    private long _planStartedMs;
    private long? _cycle;

    internal int Completed { get; private set; }
    internal int Total { get; private set; }
    internal string? Action { get; private set; }

    internal void Prepare(NokkoPlan plan, long logicalStartMs, bool restart)
    {
        if (restart)
        {
            _cycle = null;
            Completed = Total = 0;
            Action = null;
        }
        _rotations.Clear();
        _nextTransfer = 0;
        var presses = plan.Plan.Events.Where(input => input.Type == InputKind.KeyDown).ToArray();
        _transfers = presses.Where(input => input.Action is "Enter Operator" or "Return to Nokko").ToArray();
        for (int first = 0; first < presses.Length;)
        {
            long marker = Marker(first);
            int last = first + 1;
            while (last < presses.Length && Marker(last) == marker)
                last++;
            int count = last - first;
            _rotations.Add(presses[first].Number, new(marker, count));
            if (first == 0 && (_cycle is null || _cycle == marker))
                Total = Completed + count;
            first = last;
        }

        long Marker(int i)
        {
            var press = presses[i];
            long mushrooms = plan.AfterPress[press.ActionId].Mushrooms;
            if (mushrooms != long.MaxValue)
                return mushrooms;
            // Without mushrooms, group a rotation by its 30-second window and keep an Operator pair together.
            int offset = i > 0 && press.Action == "Return to Nokko" && presses[i - 1].Action == "Enter Operator"
                ? presses[i - 1].OffsetMs : press.OffsetMs;
            return (logicalStartMs + offset) / 30_000;
        }
    }

    internal void Start(long elapsedMs) => _planStartedMs = elapsedMs;

    internal void Observe(EventSample sample)
    {
        var press = sample.Event;
        if (press.Type != InputKind.KeyDown)
            return;
        if (_rotations.TryGetValue(press.Number, out var rotation))
        {
            if (_cycle != rotation.Marker)
                Completed = 0;
            _cycle = rotation.Marker;
            Total = Completed + rotation.Count;
        }
        while (_nextTransfer < _transfers.Length && _transfers[_nextTransfer].Number <= press.Number)
            _nextTransfer++;
        if (sample.Result == SendResult.Sent)
        {
            Completed++;
            Action = press.Action;
        }
    }

    internal long? OperatorDue(NokkoProgress? progress, long pausedMs)
    {
        if (progress is not { } active)
            return null;
        string action = active.Return.HasValue ? "Return to Nokko" : "Enter Operator";
        for (int i = _nextTransfer; i < _transfers.Length; i++)
            if (_transfers[i].Action == action)
                return _planStartedMs + _transfers[i].OffsetMs;
        long due = active.Return ?? active.Operator;
        return due == long.MaxValue ? null : due + pausedMs;
    }
}
