using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The production <see cref="INewsEmbeddingSimilarity"/> (gh#852, R-2): the two <b>by-owner</b> vector reads over the
/// scoped <see cref="TradingCopilotDbContext"/> that let the news feed and the relevance pass rank their own known
/// candidates in-process (data dictionary §10). The ranked nearest-neighbour read moved to
/// <see cref="PgVectorEmbeddingRecall"/> in gh#1065, where it is kind-parameterised.
/// </summary>
/// <remarks>
/// <para>
/// <b>Relational-only, by design (gh#109).</b> <see cref="EmbeddingRecord"/>'s <c>Vector</c> column has no
/// in-memory-provider mapping — <see cref="TradingCopilotDbContext"/> <c>Ignore()</c>s the entity off Postgres — so
/// these reads exist only on the relational provider. Coverage is therefore integration-tier (QA #855), which is
/// exactly why the seam's consumers — the news feed's <see cref="SemanticSalienceAxis"/> and the relevance pass's
/// semantic topic match — depend on the <see cref="INewsEmbeddingSimilarity"/> seam and not on this type: the decision
/// logic stays unit-testable against a fake while the real read is proven against real Postgres.
/// </para>
/// <para>
/// <b>Filter before rank.</b> The owner-kind predicate leads (the store's index is <c>(OwnerKind, RecordedAt)</c>), so
/// a read is "this owner kind's vectors", never "any kind's" — soft-signal embeddings share one polymorphic table with
/// topics, suggestions, journal entries, rules and market snapshots. These reads fetch by key and never order by
/// distance, so unlike the ranked recall they do not depend on a partial HNSW index and the owner kind may safely be a
/// parameter here.
/// </para>
/// <para>
/// <b>Current-model reads (gh#881).</b> Both reads filter on <c>Model == provider.Model</c>. The store keys on
/// <c>(OwnerKind, OwnerId, Model)</c> and lets a retired model's vectors coexist until swept, but vectors from
/// different models are not comparable — so an unfiltered by-owner read returns a duplicate row per owner (the gh#854
/// last-wins collapse then picks an arbitrary model's vector) and a cross-model cosine is a meaningless-but-nonzero
/// match. Pinning the current model makes <c>(OwnerKind, OwnerId)</c> unique in the result and keeps every comparison
/// same-model. After a model change, an owner embedded only under the old model reads back empty — a bounded degrade
/// — until the embedding pass re-embeds it under the current model (its candidate query already keys on the current
/// model) and sweeps that owner's stale-model rows (gh#889), so the transition self-heals rather than ever returning a
/// wrong answer. Orphaned-owner rows and crash-leaked stale-model rows are swept periodically (gh#902 / gh#915).
/// </para>
/// </remarks>
public sealed class PgVectorNewsSimilarity : INewsEmbeddingSimilarity
{
    private readonly TradingCopilotDbContext _database;
    private readonly IEmbeddingProvider _provider;

    /// <summary>Creates the similarity read over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="provider">The embedding provider — its <see cref="IEmbeddingProvider.Model"/> scopes every read to the current model's vectors (gh#881 by-owner, gh#889 nearest-N).</param>
    public PgVectorNewsSimilarity(TradingCopilotDbContext database, IEmbeddingProvider provider)
    {
        _database = database;
        _provider = provider;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredEmbedding>> GetVectorsAsync(
        IReadOnlyCollection<string> ownerIds, CancellationToken cancellationToken) =>
        ReadVectorsAsync(EmbeddingOwnerKind.SoftSignal, ownerIds, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredEmbedding>> GetTopicVectorsAsync(
        IReadOnlyCollection<string> topicNames, CancellationToken cancellationToken) =>
        ReadVectorsAsync(EmbeddingOwnerKind.Topic, topicNames, cancellationToken);

    // A plain relational by-owner read of one owner kind's CURRENT-MODEL rows for the requested owners -- no
    // CosineDistance, no ordering (gh#853, gh#854; the Model == provider.Model filter is gh#881). The owner-kind
    // predicate leads, matching the store's (OwnerKind, ...) index; the model predicate makes (OwnerKind, OwnerId)
    // unique in the result (the key is (OwnerKind, OwnerId, Model)), so a model change can't return a duplicate row
    // per owner or a cross-model vector. The RANKING is done in-process by the pure EmbeddingSimilarity helper, so
    // the caller scores its KNOWN candidates rather than searching for a global nearest set that might miss a
    // recent-but-near item. Relational-only (the Vector column has no in-memory provider mapping, gh#109), so the
    // read itself is proven by the paired QA card while the ranking is unit-tested. One helper serves both the
    // SoftSignal (news) and Topic reads; the EmbeddingOwnerKind enum stays inside this Api-tier impl.
    private async Task<IReadOnlyList<StoredEmbedding>> ReadVectorsAsync(
        EmbeddingOwnerKind ownerKind, IReadOnlyCollection<string> ownerIds, CancellationToken cancellationToken)
    {
        List<EmbeddingRecord> rows = await _database.Embeddings
            .Where(embedding => embedding.OwnerKind == ownerKind)
            .Where(embedding => embedding.Model == _provider.Model)
            .Where(embedding => ownerIds.Contains(embedding.OwnerId))
            .ToListAsync(cancellationToken);

        // Vector -> plain float[] happens in memory: Pgvector.Vector.ToArray is a client call, not translatable, and
        // keeping the seam's contract a plain float list leaves the domain-side ranking Pgvector-free.
        return [.. rows.Select(embedding => new StoredEmbedding(embedding.OwnerId, embedding.Embedding.ToArray()))];
    }
}
