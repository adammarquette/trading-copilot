namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Whether news ingestion runs, and how often (gh#358, R-2). Bound from the <c>News</c> config section.
/// </summary>
/// <remarks>
/// <b>Opt-in and off by default</b> — like the bar backfill, unconfigured means idle. News is multi-source
/// (Finnhub + Tiingo) but has no per-instrument watchlist — a source returns the whole feed and relevance mapping
/// happens downstream (gh#359) — so a single <see cref="Enabled"/> switch gates the poller rather than a symbol
/// list. The concrete provider adapters (their own <c>MarqSpec.Client.*</c> submodules) arrive as follow-ons; until
/// one is registered, an enabled poller finds no sources and writes nothing.
/// </remarks>
public sealed class NewsIngestionOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "News";

    /// <summary>Whether to poll news at all. Off by default: unconfigured means idle, never "fetch everything".</summary>
    public bool Enabled { get; init; }

    /// <summary>How often the poller runs. News moves slower than quotes, so the default cadence is looser.</summary>
    public int PollIntervalSeconds { get; init; } = 300;

    /// <summary>The poll cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    /// <summary>
    /// How far back each poll re-fetches. Wider than the interval so a pass overlaps the previous one — a missed
    /// poll self-heals, and the dedup key makes the overlap idempotent (no duplicate rows).
    /// </summary>
    public int LookbackMinutes { get; init; } = 60;

    /// <summary>Whether news ingestion is switched on.</summary>
    public bool IsConfigured => Enabled;
}
