using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The concrete <see cref="IEmbeddingOrphanStore"/> — set-based anti-join DELETEs over the polymorphic embedding
/// table: orphaned-owner rows one owner kind at a time (gh#902), and crash-leaked stale-model duplicates (gh#915).
/// Relational-only like the embed pass (gh#109): the <c>Vector</c> column has no in-memory-provider mapping, so these
/// are exercised only at the QA integration tier (#902 / #915's paired cards).
/// </summary>
public sealed class EmbeddingOrphanStore : IEmbeddingOrphanStore
{
    private readonly TradingCopilotDbContext _database;

    /// <summary>Creates the store over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    public EmbeddingOrphanStore(TradingCopilotDbContext database) => _database = database;

    /// <inheritdoc />
    public Task<int> DeleteOrphansAsync(EmbeddingOwnerKind ownerKind, CancellationToken cancellationToken) => ownerKind switch
    {
        // A row is orphaned when its OwnerId has no live producer. ExecuteDeleteAsync is ONE atomic SQL statement, so
        // the NOT-EXISTS anti-join is evaluated at delete time: a concurrent embed pass cannot be raced -- it only
        // embeds owners that exist, which this never deletes -- and a targeted set delete needs no change tracker.
        EmbeddingOwnerKind.SoftSignal => _database.Embeddings
            .Where(embedding => embedding.OwnerKind == EmbeddingOwnerKind.SoftSignal
                && !_database.News.Any(news => news.DedupKey == embedding.OwnerId))
            .ExecuteDeleteAsync(cancellationToken),

        EmbeddingOwnerKind.Topic => _database.Embeddings
            .Where(embedding => embedding.OwnerKind == EmbeddingOwnerKind.Topic
                && !_database.NewsTopics.Any(topic => topic.Name == embedding.OwnerId))
            .ExecuteDeleteAsync(cancellationToken),

        // Producer-less kinds (Suggestion / Rule / MarketSnapshot) and Unknown have no producer to check an owner
        // against, so they are never swept -- EmbeddingOrphanSweep.SweepableKinds keeps them out of the caller's loop.
        // This guard is belt-and-braces should the allow-list and this switch ever drift apart.
        _ => throw new ArgumentOutOfRangeException(
            nameof(ownerKind), ownerKind, "Only producer-backed embedding owner kinds can be swept for orphans."),
    };

    /// <inheritdoc />
    public Task<int> DeleteStaleModelDuplicatesAsync(string currentModel, CancellationToken cancellationToken) =>
        // Delete a stale-model row ONLY where a current-model row exists for the SAME owner. The self-EXISTS is the
        // safety: an owner's sole vector (whatever its model) has no current-model sibling, so it is never touched --
        // only a redundant duplicate the gh#889 sweep should have removed but a crash left behind. One atomic DELETE,
        // so it needs no change tracker and cannot race a concurrent re-embed (a row being written for the current
        // model only ever adds the sibling this looks for). Owner-kind-agnostic: the current-model sibling proves
        // legitimacy without a producer lookup, so no allow-list is needed here (unlike the orphaned-owner sweep).
        _database.Embeddings
            .Where(stale => stale.Model != currentModel
                && _database.Embeddings.Any(current => current.OwnerKind == stale.OwnerKind
                    && current.OwnerId == stale.OwnerId
                    && current.Model == currentModel))
            .ExecuteDeleteAsync(cancellationToken);
}
