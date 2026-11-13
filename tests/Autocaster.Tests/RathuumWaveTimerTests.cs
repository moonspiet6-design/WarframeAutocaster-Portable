using Autocaster.Core;

namespace Autocaster.Tests;

public sealed class RathuumWaveTimerTests
{
    private static readonly string[] Names = ["Nok", "Reth", "Zura", "Garesh", "Gorth", "Dhurnam", "Harkonar", "Vay Molta"];

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void MatchesGroupedScoreAndSeparatelyBoundsTheFirstAndLastRespawn(int waveSize)
    {
        var timer = new RathuumWaveTimer(new(waveSize));
        var forecast = FullWave(timer, waveSize);
        Assert.Equal(RathuumWaveStatus.Ready, timer.State.Status);
        Assert.Equal(waveSize, forecast.ScoreMatchedKills);
        Assert.Equal(new RathuumTimeRange(At(8942), At(9033)), forecast.FirstRespawn);
        Assert.Equal(new RathuumTimeRange(At(9006), At(9097)), forecast.LastRespawn);
        Assert.Equal(new RathuumTimeRange(At(8792), At(8883)), forecast.LaunchWindow);
        Assert.Equal(At(8837.5), forecast.SuggestedLaunch);
    }

    [Theory]
    [InlineData(1344, false)]
    [InlineData(1616, true)]
    public void FirstKillUsesTheScoreTimestampAndDoesNotWaitForTheOtherKills(int confirmation, bool late)
    {
        var options = new RathuumGateOptions { FirstKillOnly = true, FirstKillLeadMs = 360, RespawnMs = 7700 }.ToTimingOptions();
        var timer = new RathuumWaveTimer(options);
        var tracker = new RathuumLaunchTracker(options);
        RathuumWaveForecast? result = null;
        for (int now = 0; now <= 8320; now += 16)
        {
            int score = now < 1008 ? 0 : 1;
            RathuumFeedEvent[] hits = now == confirmation ? [Kill("Nok", confirmation - 16, confirmation)] : [];
            result = timer.Observe(At(now), hits, score, [], true);
            tracker.Observe(At(now), At(now), hits, score, [], true);
        }
        Assert.NotNull(result);
        Assert.Equal(1, tracker.State.NamedKills);
        Assert.Equal(RathuumWaveStatus.Ready, tracker.State.Status);
        Assert.Equal(At(8327.5), result.Value.SuggestedLaunch);
        Assert.Equal(late, result.Value.UsedLateFeedMatching);
        Assert.Equal(At(350), result.Value.FirstRespawn.Midpoint - (result.Value.SuggestedLaunch + At(10)));
        Assert.True(tracker.TryConsume(At(8328)));
    }

    [Fact]
    public void WideFirstScoreWindowUsesFeedTimingInsteadOfWithholdingTheLaunch()
    {
        var options = new RathuumGateOptions { FirstKillOnly = true, FirstKillLeadMs = 360, RespawnMs = 7700 }.ToTimingOptions();
        var tracker = new RathuumLaunchTracker(options);
        for (int now = 0; now <= 8288; now += 16)
        {
            int? score = now <= 800 ? 0 : now < 1040 ? null : 1;
            RathuumFeedEvent[] hits = now == 1344 ? [Kill("Nok", 1328, 1344)] : [];
            tracker.Observe(At(now), At(now), hits, score, [], true);
        }
        Assert.Equal(RathuumWaveStatus.Ready, tracker.State.Status);
        Assert.Contains("refined using the feed", tracker.State.Reason);
        Assert.Equal(At(8293), tracker.NextLaunch(At(8288)));
        Assert.True(tracker.TryConsume(At(8293)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void AdditionalKillsDoNotMoveOrCancelTheConfirmedFirstKillLaunch(int kills)
    {
        var options = new RathuumGateOptions { FirstKillOnly = true, FirstKillLeadMs = 360, RespawnMs = 7700 }.ToTimingOptions();
        var tracker = new RathuumLaunchTracker(options);
        tracker.BeginWave(At(0), 0);

        for (int now = 16; now <= 1344; now += 16)
        {
            int score = now < 1040 ? 0 : kills - 1;
            RathuumFeedEvent[] hits = now == 1344 ? [Kill("Nok", 1200, now)] : [];
            tracker.Observe(At(now), At(now), hits, score, [], true);
        }

        var launch = Assert.IsType<TimeSpan>(tracker.NextLaunch(At(1344)));
        for (int now = 1360; now < launch.TotalMilliseconds; now += 16)
        {
            // Another name can match the same grouped score change. The final point
            // is visible just before dispatch, while its second read is still pending.
            RathuumFeedEvent[] hits = now == 1392 ? [Kill("Zura", 1376, now)] : [];
            tracker.Observe(At(now), At(now), hits, kills - 1, [], true);
        }

        tracker.Observe(launch, launch, [], kills, [], true);
        Assert.Equal(launch, tracker.NextLaunch(launch));
        Assert.True(tracker.TryConsume(launch));
        Assert.False(tracker.TryConsume(launch));
    }

    [Fact]
    public void ReachingTheMatchLimitCancelsAConfirmedFirstKillLaunch()
    {
        var options = new RathuumGateOptions { FirstKillOnly = true }.ToTimingOptions();
        var tracker = new RathuumLaunchTracker(options);
        tracker.BeginWave(At(0), 23);
        for (int now = 16; now <= 1344; now += 16)
        {
            int score = now < 1008 ? 23 : 24;
            RathuumFeedEvent[] hits = now == 1344 ? [Kill("Nok", 1328, now)] : [];
            tracker.Observe(At(now), At(now), hits, score, [], true);
        }

        Assert.NotNull(tracker.NextLaunch(At(1344)));
        tracker.Observe(At(1360), At(1360), [], 25, [], true);
        tracker.Observe(At(1376), At(1376), [], 25, [], true);
        Assert.Equal(RathuumWaveStatus.MatchComplete, tracker.State.Status);
        Assert.Null(tracker.NextLaunch(At(1376)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(7, true)]
    public void FirstKillCanUseAGroupedScoreIncreaseThatIncludesASummon(int scoreIncrease, bool expected)
    {
        var options = new RathuumGateOptions { FirstKillOnly = true, RespawnMs = 7800, FirstKillLeadMs = 260 }.ToTimingOptions();
        var tracker = new RathuumLaunchTracker(options);
        tracker.BeginWave(At(0), 5);
        for (int now = 16; now <= 1344; now += 16)
        {
            int score = now < 1008 ? 5 : 5 + scoreIncrease;
            RathuumFeedEvent[] hits = now == 1344
                ? [new(RathuumFeedEventKind.Summon, "Hyekka", At(1312), At(now), 0), Kill("Nok", 1328, now)]
                : [];
            tracker.Observe(At(now), At(now), hits, score, [], true);
        }

        Assert.Equal(1, tracker.State.NamedKills);
        if (!expected)
        {
            Assert.Null(tracker.NextLaunch(At(1344)));
            return;
        }

        var launch = Assert.IsType<TimeSpan>(tracker.NextLaunch(At(1344)));
        for (int now = 1360; now < launch.TotalMilliseconds; now += 16)
            tracker.Observe(At(now), At(now), [], 5 + scoreIncrease, [], true);
        Assert.True(tracker.TryConsume(launch));
    }

    [Fact]
    public void FirstKillSkipsASeparateSummonButCannotUseALaterNameForAnUnknownEarlierKill()
    {
        var options = new RathuumTimingOptions { FirstKillOnly = true };
        var unresolved = new RathuumWaveTimer(options);
        var confirmed = new RathuumWaveTimer(options);
        for (int now = 0; now <= 2080; now += 16)
        {
            int score = now < 512 ? 0 : now < 1520 ? 1 : 2;
            RathuumFeedEvent[] hits = now == 1856 ? [Kill("Nok", 1840, 1856)] : [];
            unresolved.Observe(At(now), hits, score, [], true);
            if (now == 848)
                hits = [new(RathuumFeedEventKind.Summon, "Hyekka", At(832), At(848), 0)];
            confirmed.Observe(At(now), hits, score, [], true);
        }
        Assert.Null(unresolved.EligibleForecast);
        Assert.Equal(RathuumWaveStatus.Ready, confirmed.State.Status);
        Assert.Equal(At(9499.5), confirmed.EligibleForecast?.FirstRespawn.Midpoint);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANewExecutionerCanArmWhenOcrOnlyReadsThePostKillScore(bool warmBaseline)
    {
        var options = new RathuumGateOptions { FirstKillOnly = true, StartAtCurrentScore = true }.ToTimingOptions();
        var tracker = new RathuumLaunchTracker(options, At(0));
        if (warmBaseline)
            tracker.BeginWave(At(0), 3);
        for (int now = 16; now <= 1264; now += 16)
        {
            int? score = warmBaseline || now >= 816 ? 3 : null;
            RathuumFeedEvent[] hits = now == 384 ? [Kill("Garesh", 368, now)] : [];
            tracker.Observe(At(now), At(now), hits, score, [], true);
            if (now <= 1232)
                Assert.Null(tracker.NextLaunch(At(now)));
        }

        var launch = Assert.IsType<TimeSpan>(tracker.NextLaunch(At(1264)));
        Assert.Contains("team score is unchanged", tracker.State.Reason);
        for (int now = 1280; now < launch.TotalMilliseconds; now += 16)
            tracker.Observe(At(now), At(now), [], null, [], false);
        Assert.True(tracker.TryConsume(launch));
    }

    [Fact]
    public void OldRowsSummonsAndRepeatedNamesCannotArmWithoutAScoreChange()
    {
        var options = new RathuumGateOptions { FirstKillOnly = true, StartAtCurrentScore = true }.ToTimingOptions();
        var tracker = new RathuumLaunchTracker(options, At(200));
        tracker.BeginWave(At(200), 3);
        for (int now = 208; now <= 1808; now += 16)
        {
            RathuumFeedEvent[] hits = now switch
            {
                384 => [Kill("Nok", 352, now)],
                576 => [new(RathuumFeedEventKind.Summon, "Hyekka", At(544), At(now), 0)],
                608 => [new(RathuumFeedEventKind.RepeatedName, "Zura", At(592), At(now), 0)],
                _ => []
            };
            tracker.Observe(At(now), At(now), hits, 3, [], true);
        }

        Assert.Equal(0, tracker.State.NamedKills);
        Assert.Null(tracker.NextLaunch(At(1808)));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void ConfirmedFirstKillUsesRecentGameFramesEvenIfTheScoreIsObscured(bool loseCapture, bool staleFrame, bool released)
    {
        var options = new RathuumGateOptions { FirstKillOnly = true }.ToTimingOptions();
        var tracker = new RathuumLaunchTracker(options);
        tracker.BeginWave(At(0), 0);
        for (int now = 16; now <= 1344; now += 16)
        {
            RathuumFeedEvent[] hits = now == 1344 ? [Kill("Nok", 1328, now)] : [];
            tracker.Observe(At(now), At(now), hits, now < 1008 ? 0 : 1, [], true);
        }

        var launch = Assert.IsType<TimeSpan>(tracker.NextLaunch(At(1344)));
        for (int now = 1360; now < launch.TotalMilliseconds; now += 16)
            if (!staleFrame || (launch - At(now)).TotalMilliseconds > 400)
                tracker.Observe(At(now), At(now), [], null, [], false);
        if (loseCapture)
            tracker.LoseCapture();
        Assert.Equal(released, tracker.TryConsume(launch));
    }

    [Fact]
    public void StartingMidMatchCannotInventAWaveBaseline()
    {
        var timer = new RathuumWaveTimer(new());
        var hit = Kill("Nok", 120, 136);
        timer.Observe(At(112), [], 13, [], true);
        Assert.Null(timer.Observe(At(136), [hit], 13, ["Nok"], true));
        Assert.Equal(RathuumWaveStatus.AwaitingBaseline, timer.State.Status);
        Assert.Equal(13, timer.State.Score);
        Assert.Null(timer.State.StartingScore);
    }

    [Fact]
    public void StartingFromCurrentScoreRequiresStableEvidenceAndExplicitOptIn()
    {
        var timer = new RathuumWaveTimer(new() { StartAtCurrentScore = true });
        timer.Observe(At(0), [], 7, [], true);
        timer.Observe(At(16), [], 7, [], true);
        Assert.Equal(RathuumWaveStatus.AwaitingBaseline, timer.State.Status);
        timer.Observe(At(32), [], 7, [], true);
        Assert.Equal(RathuumWaveStatus.Gathering, timer.State.Status);
        Assert.Equal(7, timer.State.StartingScore);
    }

    [Fact]
    public void WarmScoreCanStartTheWaveImmediatelyAndExpiresBeforeItBecomesStale()
    {
        var cache = new RathuumScoreCache();
        cache.Observe(At(0), 5);
        cache.Observe(At(67), 5);
        Assert.Null(cache.Read(At(67)));
        cache.Observe(At(134), 5);
        int score = Assert.IsType<int>(cache.Read(At(145)));
        var tracker = new RathuumLaunchTracker(new() { StartAtCurrentScore = true });
        tracker.BeginWave(At(145), score);
        Assert.Equal(5, tracker.State.StartingScore);
        Assert.Equal(5, cache.Read(At(255)));
        Assert.Null(cache.Read(At(485)));
        cache.Observe(At(200), 6);
        Assert.Null(cache.Read(At(205)));
        cache.Observe(At(267), 6);
        cache.Observe(At(334), 6);
        Assert.Equal(6, cache.Read(At(335)));
        cache.Clear();
        Assert.Null(cache.Read(At(335)));
    }

    [Fact]
    public void AConflictingReadRestartsTheCurrentScoreCheck()
    {
        var timer = new RathuumWaveTimer(new() { StartAtCurrentScore = true });
        timer.Observe(At(0), [], 7, [], true);
        timer.Observe(At(16), [], 7, [], true);
        timer.Observe(At(32), [], 8, [], true);
        timer.Observe(At(48), [], 8, [], true);
        Assert.Null(timer.State.StartingScore);
        timer.Observe(At(64), [], 8, [], true);
        Assert.Equal(8, timer.State.StartingScore);
    }

    [Fact]
    public void AShortUnreadableScoreGapCanBeConfirmedButAConflictingValueCannot()
    {
        var timer = new RathuumWaveTimer(new());
        timer.BeginWave(At(0), 0);
        timer.Observe(At(16), [], 1, [], true);
        timer.Observe(At(32), [], null, [], true);
        timer.Observe(At(48), [], 1, [], true);
        Assert.Equal(1, timer.State.Score);
        timer.Observe(At(64), [], 2, [], true);
        timer.Observe(At(80), [], 3, [], true);
        timer.Observe(At(96), [], 2, [], true);
        Assert.Equal(1, timer.State.Score);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamesWithoutMatchingScoreTimesCannotQualify(bool signalTiming)
    {
        var timer = new RathuumWaveTimer(new() { ReadCorrection = signalTiming ? At(350) : null });
        for (int now = 0; now <= 1504; now += 16)
        {
            int score = now < 512 ? 0 : 5;
            var events = now == 1488 ? Names.Take(5).Select((name, index) => Kill(name, 1472, 1488, index + 1)).ToArray() : [];
            timer.Observe(At(now), events, score, [], true);
        }
        Assert.Equal(RathuumWaveStatus.Tentative, timer.State.Status);
        Assert.Contains("do not fit", timer.State.Reason);
    }

    [Theory]
    [InlineData(5, 1, true)]
    [InlineData(8, 1, true)]
    [InlineData(5, 3, false)]
    [InlineData(8, 4, false)]
    public void LateNamesCanConfirmACompletePackWithoutMovingTheFirstSignal(int waveSize, int lateNames, bool ready)
    {
        var options = new RathuumGateOptions { WaveSize = waveSize, ReadCorrectionMs = 350 }.ToTimingOptions();
        var timer = new RathuumWaveTimer(options);
        var tracker = new RathuumLaunchTracker(options);
        int earlyNames = waveSize - lateNames;
        for (int now = 0; now <= 8192; now += 16)
        {
            int score = now < 192 ? 0 : now < 608 ? 3 : waveSize;
            RathuumFeedEvent[] hits = now switch
            {
                544 => Names.Take(Math.Min(3, earlyNames)).Select(name => Kill(name, 528, 544)).ToArray(),
                944 => Names.Skip(3).Take(Math.Max(0, earlyNames - 3)).Select(name => Kill(name, 928, 944)).ToArray(),
                1328 => Names.Skip(earlyNames).Take(lateNames).Select(name => Kill(name, 1312, 1328)).ToArray(),
                _ => []
            };
            timer.Observe(At(now), hits, score, [], true);
            tracker.Observe(At(now), At(now), hits, score, [], true);
        }
        var scheduled = At(544 + 8000 - 350);
        Assert.Equal(ready, tracker.TryConsume(scheduled));
        if (ready)
        {
            var forecast = Assert.IsType<RathuumWaveForecast>(timer.EligibleForecast);
            Assert.True(forecast.UsedLateFeedMatching);
            Assert.Equal(waveSize, forecast.ScoreMatchedKills);
            Assert.Equal(scheduled, forecast.SuggestedLaunch);
        }
        else
            Assert.Null(timer.EligibleForecast);
    }

    [Fact]
    public void SameNameRowsAndAnUnresolvedPartialWaveDoNotProduceTiming()
    {
        var duplicate = new RathuumWaveTimer(new());
        duplicate.BeginWave(At(0), 3);
        duplicate.Observe(At(400), [Kill("Nok", 384, 400)], 4, ["Nok"], true);
        var repeated = new RathuumFeedEvent(RathuumFeedEventKind.RepeatedName, "Nok", At(400), At(416), 1);
        Assert.Null(duplicate.Observe(At(416), [repeated], 4, ["Nok", "Nok"], true));
        Assert.Equal(RathuumWaveStatus.Uncertain, duplicate.State.Status);

        var partial = new RathuumWaveTimer(new());
        partial.BeginWave(At(0), 0);
        partial.Observe(At(1360), [Kill("Nok", 1344, 1360)], 1, ["Nok"], true);
        Assert.Null(partial.Observe(At(9200), [], 1, [], true));
        Assert.Equal(RathuumWaveStatus.Uncertain, partial.State.Status);
    }

    [Fact]
    public void AScoreSlotThatCanBelongToASummonLeavesTheForecastTentative()
    {
        var timer = new RathuumWaveTimer(new());
        FullWave(timer, 5);
        var summon = new RathuumFeedEvent(RathuumFeedEventKind.Summon, "Hyekka", At(1376), At(1536), 5);
        var result = timer.Observe(At(1536), [summon], 5, [], true);
        Assert.NotNull(result);
        Assert.Equal(0, result.Value.ScoreMatchedKills);
        Assert.Equal(RathuumWaveStatus.Tentative, timer.State.Status);
    }

    [Fact]
    public void AFlashDoesNotConfirmDisappearanceAndLaterVisibleTextRevokesIt()
    {
        var timer = new RathuumWaveTimer(new());
        FullWave(timer, 5);
        for (int now = 1536; now <= 6608; now += 16)
        {
            bool flash = now is >= 6144 and <= 6320;
            IReadOnlyList<string> visible = flash ? [] : now < 6384 ? Names[..5] : Names[1..5];
            var forecast = timer.Observe(At(now), [], 5, visible, !flash);
            Assert.NotNull(forecast);
            Assert.Equal(now < 6544 ? 0 : 1, forecast.Value.ConfirmedDisappearances);
        }
        var revised = timer.Observe(At(6624), [], 5, Names[..5], true);
        Assert.NotNull(revised);
        Assert.Equal(0, revised.Value.ConfirmedDisappearances);
    }

    [Fact]
    public void AStaggeredWaveUsesTheFirstRespawnForLaunch()
    {
        var timer = new RathuumWaveTimer(new());
        var result = FullWave(timer, 5, lastDeath: 1392);
        Assert.Equal(RathuumWaveStatus.Ready, timer.State.Status);
        Assert.True(result.LaunchWindow.Latest < result.FirstRespawn.Earliest);
        Assert.True(result.FirstRespawn.Latest < result.LastRespawn.Earliest);
        Assert.Equal(result.FirstRespawn.Midpoint - At(150), result.SuggestedLaunch);
        Assert.Equal(5, result.ScoreMatchedKills);
    }

    [Fact]
    public void SustainedDisappearanceCanReconcileALateNameWithATimelyScore()
    {
        var timer = new RathuumWaveTimer(new());
        var initial = FullWave(timer, 5, lateName: true);
        Assert.Equal(RathuumWaveStatus.Tentative, timer.State.Status);
        Assert.False(initial.UsedDisappearanceTiming);
        RathuumWaveForecast? forecast = null;
        for (int now = 2000; now <= 6832; now += 16)
            forecast = timer.Observe(At(now), [], 5, now < 6384 ? Names[..5] : Names[..4], true);
        Assert.Equal(RathuumWaveStatus.Ready, timer.State.Status);
        Assert.NotNull(forecast);
        Assert.True(forecast.Value.UsedDisappearanceTiming);
        Assert.Equal(5, forecast.Value.ScoreMatchedKills);

        var revised = timer.Observe(At(6848), [], 5, Names[..5], true);
        Assert.NotNull(revised);
        Assert.False(revised.Value.UsedDisappearanceTiming);
        Assert.Equal(RathuumWaveStatus.Tentative, timer.State.Status);
    }

    [Fact]
    public void TemporaryOrUnobservableOcclusionCannotRepairALateName()
    {
        var timer = new RathuumWaveTimer(new());
        FullWave(timer, 5, lateName: true);
        for (int now = 2000; now <= 7104; now += 16)
        {
            bool flash = now is >= 6672 and < 6752;
            bool absent = now is >= 6192 and < 6496 || now >= 6672;
            var forecast = timer.Observe(At(now), [], 5, absent ? Names[..4] : Names[..5], !flash);
            Assert.NotNull(forecast);
            Assert.False(forecast.Value.UsedDisappearanceTiming);
            Assert.Equal(RathuumWaveStatus.Tentative, timer.State.Status);
        }
    }

    [Fact]
    public void AnAbsentScoreBlocksLateIdentityRecoveryWhenTheWholeHudCouldBeHidden()
    {
        var timer = new RathuumWaveTimer(new());
        FullWave(timer, 5, lateName: true);
        for (int now = 2000; now <= 6912; now += 16)
        {
            bool hudHidden = now >= 6384;
            var forecast = timer.Observe(At(now), [], hudHidden ? null : 5,
                hudHidden ? [] : Names[..5], true);
            Assert.NotNull(forecast);
            Assert.False(forecast.Value.UsedDisappearanceTiming);
            Assert.Equal(RathuumWaveStatus.Tentative, timer.State.Status);
        }
    }

    [Fact]
    public void AdvancesAfterTheLastRespawnAndNeverForecastsAfterTheMatchEnds()
    {
        var timer = new RathuumWaveTimer(new());
        FullWave(timer, 5);
        for (int now = 1536; now <= 9136; now += 16)
            timer.Observe(At(now), [], 5, [], true);
        Assert.Equal(2, timer.State.Wave);
        Assert.Equal(5, timer.State.StartingScore);
        timer.BeginWave(At(9152), 24);
        timer.Observe(At(9168), [], 25, [], true);
        Assert.Null(timer.Observe(At(9184), [], 25, [], true));
        Assert.Equal(RathuumWaveStatus.MatchComplete, timer.State.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveWaveSwitchPreservesKillsAndUpdatesPendingLaunch(bool switchBack)
    {
        var options = new RathuumGateOptions().ToTimingOptions();
        var tracker = new RathuumLaunchTracker(options);
        var original = FullWave(new(options), 5, tracker: tracker);
        Assert.Equal(original.SuggestedLaunch, tracker.NextLaunch(At(1520)));
        tracker.SetWaveSize(8);
        Assert.Null(tracker.NextLaunch(At(1520)));
        tracker.Observe(At(1536), At(1536), [], 5, Names[..5], true);
        Assert.Equal(5, tracker.State.NamedKills);
        Assert.Equal(RathuumWaveStatus.Gathering, tracker.State.Status);
        if (switchBack)
        {
            tracker.SetWaveSize(5);
            tracker.Observe(At(1552), At(1552), [], 5, Names[..5], true);
            Assert.Equal(original.SuggestedLaunch, tracker.NextLaunch(At(1552)));
            return;
        }

        for (int now = 1552; now <= 2176; now += 16)
        {
            var events = now == 2144 ? Names[5..].Select(name => Kill(name, 2128, 2144)).ToArray() : [];
            tracker.Observe(At(now), At(now), events, now < 1808 ? 5 : 8, Names, true);
        }
        Assert.Equal(8, tracker.State.NamedKills);
        Assert.Equal(original.SuggestedLaunch, tracker.NextLaunch(At(2176)));
        tracker.SetWaveSize(5);
        Assert.Equal(original.SuggestedLaunch, tracker.NextLaunch(At(2176)));
        for (int now = 2192; now <= 9856; now += 16)
        {
            tracker.Observe(At(now), At(now), [], 8, Names, true);
            if (now == 9200)
                Assert.True(tracker.TryConsume(At(now)));
        }
        Assert.Equal(2, tracker.State.Wave);
        for (int now = 9872; now <= 10640; now += 16)
        {
            var events = now == 10496 ? Names[..5].Select(name => Kill(name, 10480, 10496)).ToArray() : [];
            tracker.Observe(At(now), At(now), events, now < 10160 ? 8 : 13, Names[..5], true);
        }
        Assert.Equal(5, tracker.State.NamedKills);
        Assert.Equal(RathuumWaveStatus.Ready, tracker.State.Status);
        Assert.Equal(At(10496 + 7850), tracker.NextLaunch(At(10640)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(350)]
    public void ReadingCorrectionStartsFromTheFirstConfirmationAndSurvivesWaveRollover(int correction)
    {
        var options = new RathuumGateOptions { ReadCorrectionMs = correction }.ToTimingOptions();
        var tracker = new RathuumLaunchTracker(options);
        var forecast = FullWave(new(options), 5, tracker: tracker);
        Assert.Equal(At(1344), forecast.FirstKillConfirmedAt);
        var scheduled = At(1344 + 8000 - correction);
        Assert.Equal(scheduled, forecast.SuggestedLaunch);
        for (int now = 1536; now <= scheduled.TotalMilliseconds; now += 16)
            tracker.Observe(At(now), At(now), [], 5, [], true);
        Assert.False(tracker.TryConsume(scheduled - At(1)));
        Assert.Equal(scheduled, tracker.NextLaunch(scheduled));
        Assert.True(tracker.TryConsume(scheduled + At(1)));
        Assert.False(tracker.TryConsume(scheduled + At(2)));
    }

    [Fact]
    public void WideScoreBracketDoesNotBlockAMatchedFeedConfirmationDeadline()
    {
        var timing = new RathuumGateOptions { ReadCorrectionMs = 350, ScoreDelayMs = 90 }.ToTimingOptions();
        var tracker = new RathuumLaunchTracker(timing);
        var forecast = FullWave(new(timing), 5, tracker: tracker);
        Assert.True(forecast.FirstRespawn.Width > timing.MaximumLaunchUncertainty);
        Assert.Equal(5, forecast.ScoreMatchedKills);
        Assert.Equal(At(1344 + 8000 - 350), forecast.SuggestedLaunch);
        Assert.Equal(RathuumWaveStatus.Ready, tracker.State.Status);
        Assert.Equal(forecast.SuggestedLaunch, tracker.NextLaunch(At(1520)));
    }

    [Theory]
    [InlineData("feed obscured", true)]
    [InlineData("short score gap", true)]
    [InlineData("long score gap", false)]
    [InlineData("conflicting score", false)]
    public void ConfirmedSignalUsesRecentScoreEvenWhenTheFeedIsObscured(string condition, bool released)
    {
        var options = new RathuumGateOptions { ReadCorrectionMs = 350 }.ToTimingOptions();
        var tracker = new RathuumLaunchTracker(options);
        var forecast = FullWave(new(options), 5, tracker: tracker);
        var launch = forecast.SuggestedLaunch;
        for (int now = 1536; now <= launch.TotalMilliseconds; now += 16)
        {
            double remaining = (launch - At(now)).TotalMilliseconds;
            int? score = condition switch
            {
                "short score gap" when remaining < 200 => null,
                "long score gap" when remaining < 500 => null,
                "conflicting score" when remaining < 16 => 6,
                _ => 5
            };
            tracker.Observe(At(now), At(now), [], score, [], remaining >= 500);
        }
        Assert.Equal(released, tracker.TryConsume(launch));
        Assert.Equal(!released, tracker.MissedLaunch(launch + At(26)) is not null);
    }

    [Fact]
    public void ReadingCorrectionDoesNotDispatchAfterItsWindow()
    {
        var options = new RathuumGateOptions().ToTimingOptions();
        var tracker = new RathuumLaunchTracker(options);
        var forecast = FullWave(new(options), 5, tracker: tracker);
        var now = forecast.SuggestedLaunch + At(30);
        tracker.Observe(now, now, [], 5, [], true);
        Assert.Null(tracker.NextLaunch(now));
        Assert.False(tracker.TryConsume(now));
        Assert.Contains("missed the launch window", tracker.MissedLaunch(now));
    }

    [Theory]
    [InlineData("fresh", true)]
    [InlineData("stale", false)]
    [InlineData("capture lost", false)]
    [InlineData("late decision", false)]
    [InlineData("missed deadline", false)]
    public void LiveForecastOnlyReleasesOnceWithFreshTimelyEvidence(string condition, bool released)
    {
        var timing = new RathuumTimingOptions();
        var tracker = new RathuumLaunchTracker(timing);
        var forecast = FullWave(new(timing), 5, tracker: tracker, processingDelay: condition == "late decision" ? 9000 : 0);
        var launch = forecast.SuggestedLaunch;
        Assert.False(tracker.TryConsume(launch - At(1)));
        if (condition != "stale")
            for (int now = 1536; now <= launch.TotalMilliseconds; now += 16)
                tracker.Observe(At(now), condition == "late decision" ? At(now + 9000) : At(now), [], 5, [], true);
        if (condition == "capture lost")
            tracker.LoseCapture();
        var at = condition == "missed deadline" ? forecast.LaunchWindow.Latest + At(1) : launch;
        Assert.Equal(released, tracker.TryConsume(at));
        Assert.False(tracker.TryConsume(at));
    }

    private static RathuumWaveForecast FullWave(RathuumWaveTimer timer, int count, int lastDeath = 1072,
        bool lateName = false, RathuumLaunchTracker? tracker = null, int processingDelay = 0)
    {
        RathuumWaveForecast? forecast = null;
        int lastConfirmation = lastDeath + (lateName ? 800 : 336);
        for (int now = 0; now <= lastConfirmation + 112; now += 16)
        {
            int score = now < 1008 ? 0 : now < lastDeath ? count - 1 : count;
            RathuumFeedEvent[] events = now switch
            {
                1344 => Names.Take(count - 1).Select((name, index) => Kill(name, 1328, 1344, index + 1)).ToArray(),
                _ when now == lastConfirmation => [Kill(Names[count - 1], now - 16, now, count)],
                _ => []
            };
            IReadOnlyList<string> visible = now < 1344 ? [] : now < lastConfirmation ? Names[..(count - 1)] : Names[..count];
            forecast = timer.Observe(At(now), events, score, visible, true);
            tracker?.Observe(At(now), At(now + processingDelay), events, score, visible, true);
        }
        Assert.NotNull(forecast);
        return forecast.Value;
    }

    private static RathuumFeedEvent Kill(string name, int first, int confirmed, int count = 1) =>
        new(RathuumFeedEventKind.Executioner, name, At(first), At(confirmed), count);

    private static TimeSpan At(double ms) => TimeSpan.FromMilliseconds(ms);
}
