using MarqSpec.TradingCopilot.Domain.Signals;

namespace MarqSpec.TradingCopilot.Api.Signals;

/// <summary>
/// The salience tuning (gh#27, ADR-0014) — bound from the <c>Salience</c> config section. Projects to the domain
/// <see cref="SalienceParameters"/> the scorer consumes; held apart so the domain stays free of configuration.
/// </summary>
public sealed class SalienceOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Salience";

    /// <summary>Recency half-life in days: a star this old counts half as much (gh#27). Non-positive disables decay.</summary>
    public double HalfLifeDays { get; init; } = 14.0;

    /// <summary>The upper clamp on the multiplier — the most a stack of stars can raise an item.</summary>
    public double MultiplierCap { get; init; } = 5.0;

    /// <summary>The lower clamp — kept above zero so a muted item is down-weighted but never hidden (the ADR-0014 floor).</summary>
    public double MultiplierFloor { get; init; } = 0.25;

    /// <summary>Per-star weight along a shared instrument.</summary>
    public double InstrumentWeight { get; init; } = 1.0;

    /// <summary>Per-star weight along a shared topic.</summary>
    public double TopicWeight { get; init; } = 1.0;

    /// <summary>Per-star weight along a shared source (a weaker similarity signal than instrument/topic).</summary>
    public double SourceWeight { get; init; } = 0.5;

    /// <summary>Weight on the semantic-embedding axis — how much nearness to a starred item raises salience (gh#853).</summary>
    public double SemanticWeight { get; init; } = 1.0;

    /// <summary>The number of feed items returned when the caller gives no limit.</summary>
    public int DefaultFeedLimit { get; init; } = 50;

    /// <summary>The most feed items a single read may return.</summary>
    public int MaxFeedLimit { get; init; } = 200;

    /// <summary>Projects the bound options into the domain scorer's parameters.</summary>
    /// <returns>The domain parameters.</returns>
    public SalienceParameters ToParameters() =>
        new(HalfLifeDays, MultiplierCap, MultiplierFloor, InstrumentWeight, TopicWeight, SourceWeight, SemanticWeight);
}
