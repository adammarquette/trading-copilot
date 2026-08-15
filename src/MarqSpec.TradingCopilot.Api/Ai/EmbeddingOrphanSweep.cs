using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Which embedding owner kinds the orphan GC may sweep (gh#902) — an <b>allow-list</b>, deliberately not a deny-list.
/// A kind is sweepable only when there is a live producer table its <see cref="EmbeddingRecord.OwnerId"/> can be
/// checked against: <see cref="EmbeddingOwnerKind.SoftSignal"/> against <c>News.DedupKey</c> and
/// <see cref="EmbeddingOwnerKind.Topic"/> against <c>NewsTopics.Name</c>.
/// </summary>
/// <remarks>
/// <see cref="EmbeddingOwnerKind.Suggestion"/> / <see cref="EmbeddingOwnerKind.Rule"/> /
/// <see cref="EmbeddingOwnerKind.MarketSnapshot"/> have <b>no producer yet</b>, so there is nothing to prove an owner
/// gone — sweeping them would delete every such row to zero rather than reclaim orphans. Being an allow-list makes
/// that fail safe: a newly added owner kind is <b>not</b> swept until it is deliberately added here alongside its
/// producer check, so a future kind cannot be GC'd before it has one.
/// </remarks>
public static class EmbeddingOrphanSweep
{
    /// <summary>
    /// The owner kinds with a live producer, and so safe to sweep for orphans — the sweep iterates exactly these, so
    /// any kind absent here (producer-less, or not yet added) is never touched.
    /// </summary>
    public static IReadOnlyList<EmbeddingOwnerKind> SweepableKinds { get; } =
        [EmbeddingOwnerKind.SoftSignal, EmbeddingOwnerKind.Topic];
}
