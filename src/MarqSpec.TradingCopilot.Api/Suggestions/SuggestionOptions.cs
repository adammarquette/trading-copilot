namespace MarqSpec.TradingCopilot.Api.Suggestions;

/// <summary>
/// Limits for the suggestion read model (gh#540) — bound from the <c>Suggestions</c> configuration section.
/// </summary>
/// <remarks>
/// A cap rather than an unbounded list: the suggestion table is an append-only journal that only grows, so an
/// uncapped page would let one call pull the whole history. The defaults suit a single-operator deployment and are
/// validated on start, so a nonsensical configuration fails the host rather than every read.
/// </remarks>
public sealed class SuggestionOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "Suggestions";

    /// <summary>How many suggestions a list returns when the caller does not say.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>The most a single list call may return, however large a limit is asked for.</summary>
    public int MaxPageSize { get; set; } = 200;

    /// <summary>
    /// How long a newly issued suggestion stays actionable, in minutes (gh#544) — before the auto-flatten clamp.
    /// Must be positive.
    /// </summary>
    /// <remarks>
    /// A single deployment-wide default, deliberately <b>not</b> a per-trigger TTL column: that would add an
    /// operator-authored field to <c>TriggerRecord</c> and drag in the trigger-authoring endpoint, its contracts,
    /// validation and tests, for no demonstrated need. Card a per-trigger TTL if one is ever asked for.
    /// </remarks>
    public int ValidityMinutes { get; set; } = 60;

    /// <summary>The validity window as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Validity => TimeSpan.FromMinutes(ValidityMinutes);
}
