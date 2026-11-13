using System;
using System.Collections.Generic;
using System.Linq;
using Autocaster.Core;

namespace Autocaster.App;

internal sealed class NokkoQueue
{
    private sealed record Batch(NokkoBlock Block, int First, int Count, int Offset, int? ReturnMs = null);
    private sealed record Cycle(int First, List<Batch> Batches);

    private static readonly NokkoBlock[] Blocks = Enum.GetValues<NokkoBlock>();
    private readonly Dictionary<(NokkoBlock Block, int Occurrence), NokkoRow> _rows = [];
    private IReadOnlyList<Cycle> _cycles = [];
    private NokkoOptions? _previewOptions;
    private ExecutionPlan? _plan;
    private Cycle? _cycle;
    private NokkoRow? _preserved;
    private NokkoRow? _active;
    private IReadOnlyList<(Batch Batch, NokkoRow Row)> _batches = [];

    internal IReadOnlyList<NokkoRow> Rows { get; private set; } = [];
    internal int Count => _batches.Count;
    internal string? ActiveTitle => _active?.Kind;

    internal bool Refresh(ScenarioProfile profile, ExecutionPlan? plan, int sentThrough, NokkoRow? preserve)
    {
        var options = profile.Index!.Nokko!;
        bool rebuilt = !ReferenceEquals(_plan, plan) || (plan is null && _previewOptions != options);
        if (rebuilt)
        {
            _plan = plan;
            _previewOptions = plan is null ? options : null;
            var source = plan ?? new ScenarioCompiler(new(new Random(0))).PreviewNokko(profile);
            _cycles = Group(source, options);
        }
        var cycle = plan is null
            ? _cycles.FirstOrDefault(c => c.Batches.Any(b => b.Block == NokkoBlock.Operator) &&
                c.Batches.Any(b => b.Block == NokkoBlock.Roar))
                ?? _cycles.FirstOrDefault(c => c.Batches.Any(b => b.Block == NokkoBlock.Operator)) ?? _cycles.FirstOrDefault()
            : _cycles.LastOrDefault(c => c.First <= sentThrough) ?? _cycles.FirstOrDefault();

        bool changed = rebuilt || !ReferenceEquals(_cycle, cycle) || !ReferenceEquals(_preserved, preserve);
        if (changed)
        {
            _cycle = cycle;
            _preserved = preserve;
            var occurrences = new Dictionary<NokkoBlock, int>();
            var rows = new List<NokkoRow>();
            var batches = new List<(Batch, NokkoRow)>();
            if (cycle is not null)
                foreach (var batch in cycle.Batches)
                {
                    int occurrence = occurrences.GetValueOrDefault(batch.Block);
                    occurrences[batch.Block] = occurrence + 1;
                    var row = Row(batch.Block, occurrence);
                    row.Update(options, batch.Count, batch.ReturnMs);
                    rows.Add(row);
                    batches.Add((batch, row));
                }
            foreach (var block in Blocks.Where(block => !occurrences.ContainsKey(block)))
            {
                var row = Row(block, 0);
                row.Update(options, 0, null);
                rows.Add(row);
            }
            if (preserve is not null && !rows.Contains(preserve))
            {
                preserve.Update(options, 0, null);
                rows.Add(preserve);
            }
            Rows = rows;
            _batches = batches;
        }
        var active = plan is null ? null : _batches.LastOrDefault(batch => batch.Batch.First <= sentThrough).Row;
        if (!ReferenceEquals(active, _active))
        {
            ClearHighlight();
            _active = active;
            _active?.SetRunning(true);
        }
        return changed;
    }

    internal void ClearHighlight()
    {
        _active?.SetRunning(false);
        _active = null;
    }

    private NokkoRow Row(NokkoBlock block, int occurrence)
    {
        var key = (block, occurrence);
        if (!_rows.TryGetValue(key, out var row))
            _rows.Add(key, row = new(block));
        return row;
    }

    private static List<Cycle> Group(ExecutionPlan plan, NokkoOptions options)
    {
        var cycles = new List<Cycle>();
        Cycle? cycle = null;
        ScheduledInput? previous = null;
        NokkoBlock? previousBlock = null;
        int cycleStart = 0;
        bool hasMushrooms = options.Mushroom2Enabled || options.Mushroom1Enabled;
        foreach (var input in plan.Events)
        {
            if (input.Type != InputKind.KeyDown || Block(input.Action) is not { } block)
                continue;
            bool transferReturn = input.Action == "Return to Nokko" && previous?.Action == "Enter Operator";
            bool mushroomBatch = IsMushroom(block) && previous is not null &&
                (!IsMushroom(previousBlock) || input.OffsetMs - previous.OffsetMs > MaxGapAfter(previousBlock) + 30);
            if (cycle is null || mushroomBatch || (!hasMushrooms && !transferReturn && input.OffsetMs - cycleStart >= 30_000))
            {
                cycle = new(input.Number, []);
                cycles.Add(cycle);
                cycleStart = input.OffsetMs;
                previousBlock = null;
            }
            if (previousBlock == block && (block != NokkoBlock.Operator || transferReturn))
            {
                var last = cycle.Batches[^1];
                cycle.Batches[^1] = last with
                {
                    Count = last.Count + 1,
                    ReturnMs = transferReturn ? input.OffsetMs - last.Offset : null
                };
            }
            else
                cycle.Batches.Add(new(block, input.Number, 1, input.OffsetMs));
            previous = input;
            previousBlock = block;
        }
        return cycles;

        int MaxGapAfter(NokkoBlock? block)
        {
            var gap = options.MushroomCastGap(second: block == NokkoBlock.Mushroom2);
            return gap.Random ? gap.MaxMs : gap.FixedMs;
        }
    }

    private static bool IsMushroom(NokkoBlock? block) => block is NokkoBlock.Mushroom1 or NokkoBlock.Mushroom2;

    private static NokkoBlock? Block(string name) => name switch
    {
        "Mushroom 2" => NokkoBlock.Mushroom2,
        "Mushroom 1" => NokkoBlock.Mushroom1,
        "Roar" => NokkoBlock.Roar,
        "Fourth ability" => NokkoBlock.Fourth,
        "Enter Operator" or "Return to Nokko" => NokkoBlock.Operator,
        _ => null
    };
}
