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

    /// <summary>How often the projection runs. Bars arrive on a minute scale, so a minute is ample.</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>The poll cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}
