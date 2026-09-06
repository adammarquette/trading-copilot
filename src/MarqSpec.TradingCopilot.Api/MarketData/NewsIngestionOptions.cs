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
    /// <remarks>
    /// <b>Sized to provider latency, not to the poll cadence (gh#1123).</b> Measured live against the Finnhub
    /// free-tier <c>general</c> feed, the newest article in a 100-article payload was already 98 minutes old, the
    /// second 200 — the feed itself, not this poller, is what is stale. The shipped 60-minute default therefore
    /// admitted roughly 0–1% of it: not an empty result (which would at least be visible), but a "successful" pass
    /// that silently stores almost nothing forever. A day's typical volume (13–37 articles) needs about 24 hours
    /// of window to actually appear inside it, hence the 1440-minute default here.
    /// <para>
    /// <b>Decision, and the rejected alternative (gh#1123).</b> The alternative considered was dropping the
    /// <c>publishedAt &lt; since</c> filter in the adapters entirely and letting <see cref="NewsDedupKey"/> alone
    /// decide novelty. Rejected for two reasons: (1) Finnhub's <c>general</c> endpoint takes no time parameter at
    /// all — it always returns its own latest ~100 articles regardless of what is asked for — so a wider window
    /// costs this adapter nothing extra to fetch, while for Tiingo <c>since</c> is the actual (date-granular)
    /// query bound sent to the provider, so removing the filter there would not shrink cost, it would remove the
    /// only thing bounding it. (2) The filter is also a sanity backstop against a malformed or absurdly old
    /// timestamp, independent of how a source builds its request. Widening the shared window keeps both adapters
    /// on one knob with the same semantics rather than special-casing Finnhub. At the shipped
    /// <see cref="NewsIngestionService"/> cadence (<see cref="PollIntervalSeconds"/> = 300 = every 5 minutes),
    /// this costs re-checking already-known rows against <see cref="NewsDedupKey"/> — a cheap keyed lookup, not a
    /// duplicate write — bounded by the feed's own real daily volume (observed 13–37/day), not by the window size.
    /// </para>
    /// </remarks>
    public int LookbackMinutes { get; init; } = 1440;

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

    /// <summary>
    /// Whether the knobs are usable — validated <b>on start</b> (Program.cs) so a misconfiguration fails fast
    /// rather than silently no-opping (gh#1123). A non-positive <see cref="LookbackMinutes"/> puts <c>since</c> at
    /// or after "now", which reproduces the exact starvation this default just widened away from — every article
    /// dropped, every pass "succeeding" with nothing stored. A non-positive <see cref="PollIntervalSeconds"/>
    /// throws from <c>Task.Delay</c> inside the host's own retry loop, and a faulting <c>BackgroundService</c>
    /// stops the whole host process.
    /// </summary>
    public bool Validate() => LookbackMinutes > 0 && PollIntervalSeconds > 0;
}
