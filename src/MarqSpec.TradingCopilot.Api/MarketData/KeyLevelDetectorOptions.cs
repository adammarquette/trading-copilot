namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// How the key-level projection <b>host</b> runs (gh#597, R-10 / R-22). Bound from the <c>KeyLevels</c> config
/// section.
/// </summary>
/// <remarks>
/// <b>The algorithm's own knobs are deliberately not here.</b> The pivot window, price source and zone widths come
/// from <see cref="Domain.MarketData.KeyLevelOptions.Default"/> — the shipped Bjorgum defaults (gh#626) — so a
/// level's shape is decided in exactly one place and this file cannot silently disagree with it. What lives here is
/// only how the host runs: how many levels to keep per side, and how often to sweep.
/// </remarks>
public sealed class KeyLevelDetectorOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "KeyLevels";

    /// <summary>
    /// How many zones to keep per side (support / resistance) per series. Bounds the live set so a long session
    /// cannot accumulate levels without limit; the oldest beyond this are retired, the newest kept.
    /// </summary>
    public int MaxLevelsPerKind { get; init; } = 12;

    /// <summary>
    /// How often the projection sweeps, in seconds. Deliberately slower than the 60-second indicator pass: a key
    /// level forms over fifteen-plus bars and sits on no safety-critical path, so there is nothing to gain from
    /// re-detecting every minute.
    /// </summary>
    public int PollIntervalSeconds { get; init; } = 300;

    /// <summary>The poll cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}
