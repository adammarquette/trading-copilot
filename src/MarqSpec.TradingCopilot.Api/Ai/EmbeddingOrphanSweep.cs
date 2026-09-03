using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Which embedding owner kinds the orphan GC may sweep (gh#902) — an <b>allow-list</b>, deliberately not a deny-list.
/// A kind is sweepable only when there is a live producer table its <see cref="EmbeddingRecord.OwnerId"/> can be
/// checked against: <see cref="EmbeddingOwnerKind.SoftSignal"/> against <c>News.DedupKey</c>,
/// <see cref="EmbeddingOwnerKind.Topic"/> against <c>NewsTopics.Name</c>, and — since gh#1065 —
/// <see cref="EmbeddingOwnerKind.Suggestion"/> against <c>Suggestions.Id</c> and
/// <see cref="EmbeddingOwnerKind.JournalEntry"/> against <c>Trades.Id</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EmbeddingOwnerKind.Rule"/> / <see cref="EmbeddingOwnerKind.MarketSnapshot"/> have <b>no producer
/// yet</b> — the rulebook is epic gh#15, which has not started — so there is nothing to prove an owner gone, and
/// sweeping them would delete every such row to zero rather than reclaim orphans. Being an allow-list makes that
/// fail safe: a newly added owner kind is <b>not</b> swept until it is deliberately added here alongside its producer
/// check, so a future kind cannot be GC'd before it has one.
/// </para>
/// <para>
/// The converse is a real hazard too, which is why a unit test pins it: a kind that is <i>retrievable</i> but not
/// swept accumulates orphaned vectors forever, and every one of them keeps consuming a slot in the recall window
/// until the hydrate drops it — a silent recall degradation rather than a visible failure.
/// </para>
/// </remarks>
public static class EmbeddingOrphanSweep
{
    /// <summary>
    /// The owner kinds with a live producer, and so safe to sweep for orphans — the sweep iterates exactly these, so
    /// any kind absent here (producer-less, or not yet added) is never touched.
    /// </summary>
    public static IReadOnlyList<EmbeddingOwnerKind> SweepableKinds { get; } =
    [
        EmbeddingOwnerKind.SoftSignal,
        EmbeddingOwnerKind.Topic,
        EmbeddingOwnerKind.Suggestion,
        EmbeddingOwnerKind.JournalEntry,
    ];
}
