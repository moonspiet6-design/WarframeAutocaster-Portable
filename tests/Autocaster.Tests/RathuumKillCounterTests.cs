using Autocaster.Core;

namespace Autocaster.Tests;

public sealed class RathuumKillCounterTests
{
    [Fact]
    public void ConfirmsAFuzzyReadingOnTheNextFrameAndKeepsItsOriginalTime()
    {
        var counter = new RathuumKillCounter();
        Assert.Empty(counter.Observe(At(100), ["Executioner Harkoner"]));
        var kill = Assert.Single(counter.Observe(At(121), ["Executioner Harkonar"]));
        Assert.Equal(new RathuumFeedEvent(RathuumFeedEventKind.Executioner, "Harkonar", At(100), At(121), 1), kill);
    }

    [Fact]
    public void CountsDifferentNamesInTheSameFrameAndIgnoresSummons()
    {
        var counter = new RathuumKillCounter();
        string[] lines = ["Executioner Zura", "Executioner Dok Thul", "Deth Carabus", "Hyekka", "Reth Roller"];
        Assert.Empty(counter.Observe(At(100), lines));
        var events = counter.Observe(At(133), lines);
        Assert.Equal(2, events.Count(item => item.Kind == RathuumFeedEventKind.Executioner));
        Assert.Equal(3, events.Count(item => item.Kind == RathuumFeedEventKind.Summon));
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public void RepeatedNameDoesNotAddAKillAndCanBeCountedAfterRespawn()
    {
        var counter = new RathuumKillCounter();
        string[] rows = ["Executioner Harkonar", "Executioner Harkonar"];
        Assert.Empty(counter.Observe(At(100), rows));
        Assert.Empty(counter.Observe(At(100), rows));
        var events = counter.Observe(At(133), rows);
        Assert.Equal([RathuumFeedEventKind.Executioner, RathuumFeedEventKind.RepeatedName], events.Select(item => item.Kind));
        Assert.Empty(counter.Observe(At(165), rows));
        Assert.Empty(counter.Observe(At(2_000), [rows[0]]));
        Assert.Equal(RathuumFeedEventKind.RepeatedName, Assert.Single(counter.Observe(At(2_033), [rows[0]])).Kind);
        Assert.Equal(1, counter.Count);
        Assert.Empty(counter.Observe(At(8_300), [rows[0]]));
        Assert.Equal(RathuumFeedEventKind.Executioner, Assert.Single(counter.Observe(At(8_333), [rows[0]])).Kind);
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public void IncompleteOrStaleEvidenceCannotInventAKill()
    {
        var counter = new RathuumKillCounter();
        Assert.Empty(counter.Observe(At(100), ["Executioner", "Executioner Dork", "Nok", "Executioner Harkonar Decoy"]));
        Assert.Empty(counter.Observe(At(133), ["Executioner Harkoner"]));
        Assert.Empty(counter.Observe(At(166), ["Executioner Harkoner"]));
        Assert.Empty(counter.Observe(At(1_000), ["Executioner Harkonar"]));
        Assert.Equal(0, counter.Count);
    }

    private static TimeSpan At(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
}
