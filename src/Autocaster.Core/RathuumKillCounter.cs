using System.Text;
using System.Text.RegularExpressions;

namespace Autocaster.Core;

public enum RathuumFeedEventKind { Executioner, Summon, RepeatedName }

public readonly record struct RathuumFeedEvent(RathuumFeedEventKind Kind, string Name,
    TimeSpan FirstSeen, TimeSpan Confirmed, int ExecutionerCount);

public sealed class RathuumKillCounter
{
    private static readonly string[] Names =
        ["Dhurnam", "Dok Thul", "Garesh", "Gorth", "Harkonar", "Nok", "Reth", "Vay Molta", "Zura"];
    private static readonly string[] NormalizedNames = Names.Select(Normalize).ToArray();
    private static readonly Regex Executioner = new(@"execut(?:i[o0]ner|i[o0]ne[ftl]|[il]aner|[il][o0]her)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly TimeSpan EvidenceWindow = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan MinimumSameNameInterval = TimeSpan.FromSeconds(7);
    private readonly Dictionary<string, Appearance> _visible = [];
    private readonly Dictionary<string, TimeSpan> _lastCounted = [];
    private TimeSpan? _lastFrame;

    public int Count { get; private set; }

    public static string? ReadExecutionerName(string line) => Parse(line) is { Summon: false } match ? match.Name : null;

    public static bool LooksLikeExecutionerLine(string line) => Executioner.IsMatch(line);

    // Each call describes one distinct frame of the cyan victim text in the kill feed.
    public IReadOnlyList<RathuumFeedEvent> Observe(TimeSpan timestamp, IEnumerable<string> lines)
    {
        if (_lastFrame is TimeSpan last && timestamp <= last)
            return [];
        _lastFrame = timestamp;
        var events = new List<RathuumFeedEvent>();
        foreach (var group in lines.Select(Parse).OfType<Match>().GroupBy(match => match.Name))
        {
            bool exact = group.Any(match => match.Exact);
            bool summon = group.First().Summon;
            bool repeated = !summon && group.Count(match => match.Exact) > 1;
            if (!_visible.TryGetValue(group.Key, out var current) || timestamp - current.LastSeen > EvidenceWindow ||
                (!current.Reported && timestamp - current.FirstSeen > EvidenceWindow))
                _visible[group.Key] = current = new(timestamp);
            current.LastSeen = timestamp;
            current.Frames++;
            current.Exact |= exact;
            if (!current.Reported && current.Frames >= 2 && current.Exact)
            {
                current.Reported = true;
                var kind = RathuumFeedEventKind.Summon;
                if (!summon)
                {
                    if (_lastCounted.TryGetValue(group.Key, out var previous) &&
                        current.FirstSeen - previous < MinimumSameNameInterval)
                    {
                        kind = RathuumFeedEventKind.RepeatedName;
                        current.ReportedRepeat = true;
                    }
                    else
                    {
                        kind = RathuumFeedEventKind.Executioner;
                        _lastCounted[group.Key] = current.FirstSeen;
                        Count++;
                    }
                }
                events.Add(new(kind, group.Key, current.FirstSeen, timestamp, Count));
            }

            if (repeated)
            {
                if (timestamp - current.RepeatFirstSeen > EvidenceWindow)
                    current.RepeatFrames = 0;
                if (current.RepeatFrames++ == 0)
                    current.RepeatFirstSeen = timestamp;
                if (!current.ReportedRepeat && current.RepeatFrames >= 2)
                {
                    current.ReportedRepeat = true;
                    events.Add(new(RathuumFeedEventKind.RepeatedName, group.Key, current.RepeatFirstSeen, timestamp, Count));
                }
            }
        }
        return events;
    }

    private static Match? Parse(string line)
    {
        var prefix = Executioner.Match(line);
        if (!prefix.Success)
        {
            return Normalize(line) switch
            {
                "hyekka" or "killedhyekka" => new("Hyekka", true, true),
                "dethcarabus" or "killeddethcarabus" or "carabus" => new("Deth Carabus", true, true),
                "rethroller" or "killedrethroller" => new("Reth Roller", true, true),
                "harkonardecoy" or "killedharkonardecoy" => new("Harkonar Decoy", true, true),
                _ => null
            };
        }

        string text = Normalize(line[(prefix.Index + prefix.Length)..]);
        if (text.Length is < 3 or > 12)
            return null;
        int exact = Array.IndexOf(NormalizedNames, text);
        if (exact >= 0)
            return new(Names[exact], prefix.Value.Equals("Executioner", StringComparison.OrdinalIgnoreCase), false);

        int found = -1;
        for (int i = 0; i < NormalizedNames.Length; i++)
        {
            string name = NormalizedNames[i];
            if (name.Length < 5 || !WithinOneEdit(text, name))
                continue;
            if (found >= 0)
                return null;
            found = i;
        }
        return found >= 0 ? new(Names[found], false, false) : null;
    }

    private static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (char letter in text)
        {
            if (char.IsAsciiLetter(letter))
                result.Append(char.ToLowerInvariant(letter));
            else if (letter is '0' or '1')
                result.Append(letter == '0' ? 'o' : 'l');
        }
        return result.ToString();
    }

    private static bool WithinOneEdit(string first, string second)
    {
        if (first.Length > second.Length)
            (first, second) = (second, first);
        if (second.Length - first.Length > 1)
            return false;
        int i = 0;
        while (i < first.Length && first[i] == second[i])
            i++;
        if (i == first.Length)
            return true;
        int skip = first.Length == second.Length ? 1 : 0;
        return first.AsSpan(i + skip).SequenceEqual(second.AsSpan(i + 1));
    }

    private sealed record Match(string Name, bool Exact, bool Summon);

    private sealed class Appearance(TimeSpan firstSeen)
    {
        internal TimeSpan FirstSeen { get; } = firstSeen;
        internal TimeSpan LastSeen { get; set; } = firstSeen;
        internal int Frames { get; set; }
        internal bool Exact { get; set; }
        internal bool Reported { get; set; }
        internal int RepeatFrames { get; set; }
        internal TimeSpan RepeatFirstSeen { get; set; }
        internal bool ReportedRepeat { get; set; }
    }
}
