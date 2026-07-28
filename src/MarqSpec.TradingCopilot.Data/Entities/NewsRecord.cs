namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One stored news / soft-signal item — the <b>store of record</b> for R-2 ingestion (gh#358), the news analogue
/// of <see cref="BarRecord"/>. News is the reference template; other non-market feeds reuse this shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <c>IUserOwned</c>.</b> Raw news is shared / global reference data (R-20) — the same line
/// that keeps <see cref="BarRecord"/> global — so no tenant filter applies. The per-user <i>salience</i> that
/// reweights it (a <c>SoftSignalFeedback</c> star, gh#27) is a separate, operator-owned entity; the item itself is
/// not.
/// </para>
/// <para>
/// The key is <see cref="DedupKey"/> — a canonicalized URL (<c>NewsDedupKey</c>). It IS the idempotence guard,
/// enforced by the database rather than trusted to the writer: the same story arriving from Finnhub <i>and</i>
/// Tiingo collapses to one row (their feeds union into <see cref="SourceFeeds"/>), and an overlapping re-poll
/// updates in place instead of duplicating — exactly as the bar composite key works, for news.
/// </para>
/// <para>
/// <c>REL</c> today. The <c>pgvector</c> embedding that makes news semantically retrievable is deferred to gh#377
/// (it lands with the operator-gated Cohere / retrieval path, gh#103), so there is no vector column here yet.
/// </para>
/// </remarks>
public sealed class NewsRecord
{
    /// <summary>The canonical dedup key (a normalized URL) — the primary key and idempotence guard.</summary>
    public required string DedupKey { get; set; }

    /// <summary>The soft-signal kind — <c>news</c> today; social / transcripts reuse this shape later (R-2).</summary>
    public required string Type { get; set; }

    /// <summary>The story's URL as first stored (the human-readable original behind <see cref="DedupKey"/>).</summary>
    public required string Url { get; set; }

    /// <summary>The headline.</summary>
    public required string Title { get; set; }

    /// <summary>The provider-supplied summary or body text.</summary>
    public required string Summary { get; set; }

    /// <summary>When the story was published (UTC).</summary>
    public required DateTimeOffset PublishedAt { get; set; }

    /// <summary>The provider-tagged tickers, raw and un-mapped — relevance mapping (SPY → ES) is gh#359.</summary>
    public required List<string> Tickers { get; set; }

    /// <summary>
    /// Provenance — the source feeds that carried this story (for example <c>finnhub</c>, <c>tiingo</c>). A second
    /// source carrying the same story unions in here rather than creating a duplicate row.
    /// </summary>
    public required List<string> SourceFeeds { get; set; }

    /// <summary>
    /// When this row was last written. A cross-source union or a revision updates it, so "when we learned it"
    /// stays answerable separately from "when it published" (mirrors <see cref="BarRecord.RecordedAt"/>).
    /// </summary>
    public required DateTimeOffset RecordedAt { get; set; }

    /// <summary>
    /// The traded instruments this story bears on (gh#359) — resolved from its <see cref="Tickers"/> via the global
    /// ticker↔instrument maps, plus any instrument-scoped topic it matched. Empty until the relevance pass resolves it.
    /// </summary>
    public List<string> MatchedInstruments { get; set; } = [];

    /// <summary>The topics this story matched (gh#359), by keyword over its title + summary. Empty until resolved.</summary>
    public List<string> MatchedTopics { get; set; } = [];

    /// <summary>
    /// When relevance was last resolved for this story, or <see langword="null"/> if never. The pass resolves rows
    /// that are null or older than the relevance config's last change (gh#359).
    /// </summary>
    public DateTimeOffset? RelevanceResolvedAt { get; set; }
}
