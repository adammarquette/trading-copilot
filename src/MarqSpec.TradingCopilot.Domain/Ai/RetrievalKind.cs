namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>
/// What kind of stored context a retrieval reads (gh#1065, R-6) — the <b>domain-side</b> selector that lets a consumer
/// ask for "relevant context" rather than "relevant news", generalising gh#995's news-only slice.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a <b>separate</b> enum from the data layer's <c>EmbeddingOwnerKind</c>, not an alias of it. The store's
/// owner kinds are a <i>persistence</i> concern — they include kinds nothing retrieves (a market snapshot), they are
/// part of a persisted primary key, and their numeric values can never move. These are the kinds a <i>retrieval
/// consumer</i> may ask for, so the Domain seam carries no <c>Data</c> dependency (the same reason gh#854 kept
/// <c>GetTopicVectorsAsync</c> a named method rather than an owner-kind parameter). The mapping between the two lives
/// in the one Api-tier place that reads the store.
/// </para>
/// <para>
/// <b>News is deployment-global; the other two are owner-scoped (R-20).</b> A <see cref="News"/> row is shared
/// reference data, while a <see cref="Suggestion"/> or a <see cref="JournalEntry"/> belongs to one operator — so the
/// retrieval that hydrates them reads through the tenant query filter and another operator's row can never surface.
/// The vector recall itself is global (the embedding store is not <c>IUserOwned</c>, following its owners), which is
/// exactly why the hydrate is the enforcement point.
/// </para>
/// </remarks>
public enum RetrievalKind
{
    /// <summary>Not a retrievable kind — the refusable zero, so <c>default</c> is never silently "news".</summary>
    Unknown = 0,

    /// <summary>An ingested news / soft-signal item (R-2). <b>Deployment-global</b>: no owner filter.</summary>
    News = 1,

    /// <summary>An AI trade suggestion and its rationale (R-4). <b>Owner-scoped</b> (R-20).</summary>
    Suggestion = 2,

    /// <summary>A journal entry — a closed trade the operator took (R-9). <b>Owner-scoped</b> (R-20).</summary>
    JournalEntry = 3,
}

/// <summary>The retrievable <see cref="RetrievalKind"/> set — every real kind, never <see cref="RetrievalKind.Unknown"/>.</summary>
/// <remarks>
/// An <b>allow-list</b> a consumer can ask for wholesale ("ground this chat turn on everything I have"), kept beside
/// the enum so adding a kind is one edit rather than a hunt through call sites. The refusable zero is excluded by
/// construction, so a caller passing <see cref="All"/> can never ask the store for a kind it cannot map.
/// </remarks>
public static class RetrievalKinds
{
    /// <summary>Every retrievable kind, in a stable order.</summary>
    public static IReadOnlyList<RetrievalKind> All { get; } =
        [RetrievalKind.News, RetrievalKind.Suggestion, RetrievalKind.JournalEntry];
}
