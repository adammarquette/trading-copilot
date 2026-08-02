namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Which cross-asset context symbols to stream, and how hard to try (gh#496, of gh#411) — bound from the
/// <c>ContextIngestion</c> configuration section.
/// </summary>
/// <remarks>
/// Separate from <c>IngestionOptions</c> (the tradeable quote feed) on purpose: these are <b>context</b> symbols
/// on a data-only source, and the two lists must never be edited into one another. Credentials are never here —
/// the source's own options carry the token, from the environment (ADR-0015).
/// </remarks>
public sealed class ContextIngestionOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "ContextIngestion";

    /// <summary>
    /// The context symbols to subscribe (for example <c>SPY</c>, <c>QQQ</c>). Empty means context ingestion is
    /// <b>disabled</b> — it is opt-in, and a deployment with no Finnhub token simply leaves it unset.
    /// </summary>
    public string[] Symbols { get; init; } = [];

    /// <summary>How long to wait after a dropped subscription before re-subscribing, in seconds.</summary>
    public int ReconnectDelaySeconds { get; init; } = 5;

    /// <summary>The reconnect backoff.</summary>
    public TimeSpan ReconnectDelay => TimeSpan.FromSeconds(ReconnectDelaySeconds);
}
