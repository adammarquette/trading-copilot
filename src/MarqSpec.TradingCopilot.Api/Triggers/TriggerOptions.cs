namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// How the trigger scan runs (gh#385, R-4 / R-7). Bound from the <c>Triggers</c> config section.
/// </summary>
public sealed class TriggerOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Triggers";

    /// <summary>
    /// How often the scan runs, in seconds. Defaults to 60s: it matches the indicator projection cadence, and
    /// bar-derived indicators only change per bar — so a faster scan would only re-read values that have not moved.
    /// </summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>The poll cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}
