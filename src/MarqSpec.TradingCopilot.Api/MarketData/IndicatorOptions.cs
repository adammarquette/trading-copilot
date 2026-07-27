namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// How the indicator projection runs (gh#310, R-1). Bound from the <c>Indicators</c> config section.
/// </summary>
/// <remarks>
/// <b>There is deliberately no instrument list here.</b> The projection derives its work from the bar store —
/// whatever instrument × resolution has bars gets indicators. A second symbol list would be a second thing to
/// keep in sync with <c>Backfill:Instruments</c>, and the failure mode of drifting apart is an instrument whose
/// bars are archived but whose ATR is silently missing. The execution path would meet that as "no value", i.e. a
/// stop that never promotes — a quiet failure on a safety-adjacent path, for no gain.
/// </remarks>
public sealed class IndicatorOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Indicators";

    /// <summary>
    /// The ATR period. Wilder's default is 14, and it is the default here because that is what the operator's
    /// chart will be showing — a stop distance that disagrees with their chart is worse than no ATR.
    /// </summary>
    public int AtrPeriod { get; init; } = 14;

    /// <summary>
    /// The bar size, in minutes, whose ATR an <b>ATR promotion band</b> means (gh#311). ADR-0007 names ATR as a
    /// proximity metric but not which series it is measured on, and 2 × ATR on 1-minute bars is a very different
    /// distance from 2 × ATR on 15-minute bars — so it is stated here rather than assumed.
    /// </summary>
    /// <remarks>
    /// <b>Must be a resolution the backfill is archiving</b> (<c>Backfill:ResolutionMinutes</c>) — the default 1
    /// matches that option's own default. If it is not, no value exists, and per gh#311 an ATR-banded stop then
    /// <i>does not promote</i>. That is the safe direction, but it is silent, so the promotion watcher logs a
    /// warning naming this setting when it finds nothing.
    /// </remarks>
    public int BandResolutionMinutes { get; init; } = 1;

    /// <summary>How often the projection runs. Bars arrive on a minute scale, so a minute is ample.</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>The poll cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}
