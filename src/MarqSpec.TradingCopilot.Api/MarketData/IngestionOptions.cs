namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Which contracts to ingest live quotes for, and how the subscription behaves (gh#13, R-1). Bound from the
/// <c>Ingestion</c> config section.
/// </summary>
/// <remarks>
/// Ingestion is <b>opt-in</b>: an empty <see cref="Symbols"/> disables the host entirely, so a deployment (or a
/// local run) that does not want a live market-data subscription simply does not configure one.
/// </remarks>
public sealed class IngestionOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Ingestion";

    /// <summary>The venue-neutral symbols to subscribe to (e.g. <c>MES</c>, <c>ES</c>). Empty disables ingestion.</summary>
    public string[] Symbols { get; init; } = [];

    /// <summary>The backoff, in seconds, between a dropped subscription and the next attempt.</summary>
    public int ReconnectDelaySeconds { get; init; } = 5;

    /// <summary>The backoff as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan ReconnectDelay => TimeSpan.FromSeconds(ReconnectDelaySeconds);
}
