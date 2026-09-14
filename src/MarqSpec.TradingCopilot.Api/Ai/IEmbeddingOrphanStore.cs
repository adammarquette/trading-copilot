using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Deletes <see cref="EmbeddingRecord"/> rows whose owner no longer exists (gh#902) — the orphaned-owner half of the
/// embedding hygiene the stale-model sweep (gh#889) does not reach. One method per call so the orchestration
/// (<see cref="EmbeddingOrphanGcHost.SweepAsync"/>) can be unit-tested over a fake while the concrete anti-join DELETE
/// stays QA integration-tier (the polymorphic <c>Embeddings</c> store is relational-only, gh#109).
/// </summary>
public interface IEmbeddingOrphanStore
{
    /// <summary>
    /// Deletes the orphaned rows of one owner kind — those whose <see cref="EmbeddingRecord.OwnerId"/> has no live
    /// producer — and returns how many were removed. Only <see cref="EmbeddingOrphanSweep.SweepableKinds"/> have a
    /// producer to check against; asking for a producer-less kind is a caller error.
    /// </summary>
    /// <param name="ownerKind">The owner kind to sweep — one of <see cref="EmbeddingOrphanSweep.SweepableKinds"/>.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The number of orphaned rows deleted.</returns>
    Task<int> DeleteOrphansAsync(EmbeddingOwnerKind ownerKind, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes <b>crash-leaked stale-model duplicate</b> rows (gh#915) — a <c>Model != currentModel</c> row for an
    /// owner that <b>also</b> has a <paramref name="currentModel"/> row. That pair is the residue of a re-embed whose
    /// incremental stale-model sweep (gh#889) never ran (a crash between its <c>SaveChanges</c> and the sweep); the
    /// owner still exists, so the orphaned-owner sweep keeps it. The current-model row's presence is the safety: it
    /// proves the owner is legitimately embedded now, so its other-model row is a redundant leftover — never an
    /// owner's <i>only</i> vector, which the current-model read filter degrades over gracefully rather than loses.
    /// </summary>
    /// <param name="currentModel">The provider's current model — the one every read pins to.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The number of stale-model duplicate rows deleted.</returns>
    Task<int> DeleteStaleModelDuplicatesAsync(string currentModel, CancellationToken cancellationToken);
}
