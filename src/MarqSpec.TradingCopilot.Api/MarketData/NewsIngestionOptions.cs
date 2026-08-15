using MarqSpec.TradingCopilot.Domain.Venue;

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

    /// <summary>
    /// The minimum title Sørensen–Dice similarity for the R-2 fuzzy fallback to call two items the same story
    /// (gh#764). Configurable because gh#464 tunes it against <b>real provider feeds</b> in staging, and as a
    /// compile-time constant every experiment cost a code change and a redeploy. Defaults to
    /// <see cref="NewsFuzzyDedup.MinTitleSimilarity"/>, so an unconfigured deployment behaves exactly as before.
    /// </summary>
    /// <remarks>
    /// Lowering it trades precision for recall on a rule that is <b>deliberately precision-first</b>: a wrongly
    /// merged row destroys a distinct signal, which is worse than a duplicate that over-counts. It is not the only
    /// guard — the subset and semantic-modifier tests still hold — but it is the one that moves.
    /// </remarks>
    public double FuzzyMinTitleSimilarity { get; init; } = NewsFuzzyDedup.MinTitleSimilarity;

    /// <summary>
    /// The widest publication-time gap, in minutes, two feeds may stamp the same story with (gh#764); the second
    /// knob gh#464 tunes. Matches <see cref="NewsFuzzyDedup.MaxPublishedGap"/> by default — a unit test pins the two
    /// together so this cannot drift from the constant it mirrors.
    /// </summary>
    public int FuzzyMaxPublishedGapMinutes { get; init; } = 60;

    /// <summary>
    /// The configured gap as a <see cref="TimeSpan"/>. It governs <b>both</b> the pairwise comparison and the
    /// publication-time window the cross-pass candidate query loads — the two must be the same value, or a widened
    /// gap would accept pairs the query never fetched and the knob would appear inert past its default (gh#836).
    /// </summary>
    public TimeSpan FuzzyMaxPublishedGap => TimeSpan.FromMinutes(FuzzyMaxPublishedGapMinutes);

    /// <summary>Whether news ingestion is switched on.</summary>
    public bool IsConfigured => Enabled;
}
