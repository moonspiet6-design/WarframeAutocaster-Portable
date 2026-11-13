namespace Autocaster.Core;

public sealed record RathuumGateOptions
{
    public int WaveSize { get; init; } = 5;
    // Preserve the settings already written by the first Rathuum editor build.
    [System.Text.Json.Serialization.JsonPropertyName("leadMs")]
    public int ReadCorrectionMs { get; init; } = 150;
    public bool FirstKillOnly { get; init; }
    public int FirstKillLeadMs { get; init; } = 350;
    public int RespawnMs { get; init; } = 8000;
    public int TimeoutMs { get; init; } = 60_000;
    public bool StartAtCurrentScore { get; init; }
    public bool CompactHud { get; init; }
    public int ScanHz { get; init; } = 120;
    public bool FpsGuard { get; init; } = true;
    public int RespawnUncertaintyMs { get; init; } = 25;
    public int MaxLaunchUncertaintyMs { get; init; } = 100;
    public int ScoreDelayMs { get; init; } = 25;
    public int FeedDelayMinMs { get; init; } = 200;
    public int FeedDelayMaxMs { get; init; } = 550;
    public int LateFeedDelayMs { get; init; } = 2000;
    public int FadeDelayMinMs { get; init; } = 4800;
    public int FadeDelayMaxMs { get; init; } = 5400;

    public void Validate()
    {
        if (WaveSize is not (5 or 8) || RespawnMs is < 1000 or > 60_000 || ReadCorrectionMs is < 0 or > 5000 ||
            RespawnUncertaintyMs is < 0 or > 1000 || (!FirstKillOnly && ReadCorrectionMs >= RespawnMs) ||
            (FirstKillOnly && (FirstKillLeadMs is < 1 or > 5000 || FirstKillLeadMs >= RespawnMs - RespawnUncertaintyMs)) ||
            TimeoutMs is < 100 or > 600_000 || ScanHz is < 30 or > 144 ||
            ScoreDelayMs is < 0 or > 1000 || MaxLaunchUncertaintyMs is < 1 or > 1000)
            throw new ScenarioException("Check the kill-feed timing, timeout, and scan rate.");
        if (FeedDelayMinMs < 0 || FeedDelayMaxMs < FeedDelayMinMs || FeedDelayMaxMs >= RespawnMs ||
            LateFeedDelayMs < FeedDelayMaxMs || LateFeedDelayMs > 10_000 ||
            FadeDelayMinMs < 0 || FadeDelayMaxMs < FadeDelayMinMs || FadeDelayMaxMs > 30_000)
            throw new ScenarioException("Check the kill-feed appearance and disappearance ranges.");
    }

    public RathuumTimingOptions ToTimingOptions()
    {
        Validate();
        return new(WaveSize)
        {
            FirstKillOnly = FirstKillOnly,
            Lead = TimeSpan.FromMilliseconds(FirstKillLeadMs),
            ReadCorrection = FirstKillOnly ? null : TimeSpan.FromMilliseconds(ReadCorrectionMs),
            Respawn = TimeSpan.FromMilliseconds(RespawnMs),
            RespawnUncertainty = TimeSpan.FromMilliseconds(RespawnUncertaintyMs),
            MaximumLaunchUncertainty = TimeSpan.FromMilliseconds(MaxLaunchUncertaintyMs),
            ScoreDisplayDelay = TimeSpan.FromMilliseconds(ScoreDelayMs),
            FeedDelay = new(TimeSpan.FromMilliseconds(FeedDelayMinMs), TimeSpan.FromMilliseconds(FeedDelayMaxMs)),
            MaximumLateFeedDelay = TimeSpan.FromMilliseconds(LateFeedDelayMs),
            DisappearanceDelay = new(TimeSpan.FromMilliseconds(FadeDelayMinMs), TimeSpan.FromMilliseconds(FadeDelayMaxMs)),
            StartAtCurrentScore = StartAtCurrentScore
        };
    }
}

public readonly record struct RathuumGateReading(RathuumWaveState State, long? LaunchAt = null, string? Failure = null);

public interface IRathuumMonitor : IDisposable
{
    RathuumGateReading Read();
    bool TryConsume(long expectedLaunch);
}

public interface IRathuumTarget
{
    IRathuumMonitor StartRathuumMonitor(RathuumGateOptions options);
}
