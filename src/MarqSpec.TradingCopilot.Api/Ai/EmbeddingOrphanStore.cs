using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The concrete <see cref="IEmbeddingOrphanStore"/> — a set-based anti-join DELETE over the polymorphic embedding
/// table (gh#902), one owner kind at a time. Relational-only like the embed pass (gh#109): the <c>Vector</c> column
/// has no in-memory-provider mapping, so this is exercised only at the QA integration tier (#902's paired card).
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
}
