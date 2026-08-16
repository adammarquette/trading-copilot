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

    /// <summary>
    /// Whether the knobs are usable — validated <b>on start</b> (Program.cs) so a misconfiguration fails fast rather
    /// than at runtime. A non-positive <see cref="PollIntervalSeconds"/> throws from <c>Task.Delay</c> inside the
    /// host's retry loop, and a faulting <c>BackgroundService</c> stops the whole host process; a non-positive
    /// <see cref="MaxLevelsPerKind"/> makes every <see cref="Domain.MarketData.KeyLevels.Detect"/> pass throw — caught
    /// per series and swallowed — so the host runs but never writes a level. Neither is a safe silent default.
    /// </summary>
    public bool Validate() => PollIntervalSeconds > 0 && MaxLevelsPerKind > 0;
}
