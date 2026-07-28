namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// How often the deterministic trigger scan runs (gh#385). Bound from the <c>Triggers</c> config section.
/// </summary>
/// <remarks>
/// Like the indicator projection it mirrors, the scan is <b>always on</b> and needs no work list of its own — its
/// work is whatever enabled triggers the operator has authored, so there is nothing to opt into. The 60s default
/// matches the indicator cadence: bar-derived indicators change only per bar, so a faster scan would find nothing
/// new (a quote-driven low-latency variant is a later, additive increment).
/// </remarks>
public sealed class TriggerOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Triggers";

    /// <summary>How often the scan evaluates every enabled trigger.</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>The scan cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}
