using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The production <see cref="INewsEmbeddingSimilarity"/> (gh#852, R-2): the nearest-news read over the scoped
/// <see cref="TradingCopilotDbContext"/>, ranked by <c>pgvector</c> cosine distance to match the HNSW cosine index
/// the <see cref="EmbeddingRecord"/> store is built on (data dictionary §10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Relational-only, by design (gh#109).</b> <see cref="EmbeddingRecord"/>'s <c>Vector</c> column has no
/// in-memory-provider mapping — <see cref="TradingCopilotDbContext"/> <c>Ignore()</c>s the entity off Postgres — so
/// the <c>CosineDistance</c> operator this query orders by exists only on the relational provider. Coverage is
/// therefore integration-tier (QA #855), which is exactly why
/// <see cref="NewsSemanticSearch"/> depends on the <see cref="INewsEmbeddingSimilarity"/> seam and not on this type:
/// the decision logic stays unit-testable against a fake while the real read is proven against real Postgres.
/// </para>
/// <para>
/// <b>Filter before rank.</b> The owner-kind predicate leads (the store's index is <c>(OwnerKind, RecordedAt)</c>),
/// so this is "the nearest <i>news</i> item", never "the nearest anything" — soft-signal embeddings share one
/// polymorphic table with suggestions, rules and market snapshots.
/// </para>
/// <para>
/// <b>The soft-signal predicate must stay matchable by <c>IX_Embeddings_Vector_Cosine_SoftSignal</c></b> — the
/// <b>partial</b> HNSW index gh#864 added, whose predicate is exactly <c>OwnerKind = SoftSignal</c>. That index is
/// what makes this read's recall sound rather than merely fast: against the table-wide vector index, Postgres may
/// serve the <c>ORDER BY … LIMIT</c> from the HNSW graph and apply the owner kind as a <i>post-scan filter</i>, so
/// a crowd of closer other-kind rows fills the approximate candidate window and the read returns <b>zero</b>
/// neighbours though thousands exist (reproduced at 15,000 rows by gh#861's suite). Searching a soft-signal-only
/// index leaves nothing for a post-filter to discard.
/// </para>
/// <para>
/// So this is a query whose correctness depends on a schema object: parameterising the owner kind, widening the
/// predicate, or dropping the partial index re-opens the starvation. If another owner kind ever needs a
/// vector-ordered read, it wants <b>its own</b> partial index rather than a shared table-wide one.
/// </para>
/// </remarks>
public sealed class PgVectorNewsSimilarity : INewsEmbeddingSimilarity
{
    private readonly TradingCopilotDbContext _database;

    /// <summary>Creates the similarity read over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    public PgVectorNewsSimilarity(TradingCopilotDbContext database) => _database = database;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SemanticNeighbor>> NearestNewsAsync(
        IReadOnlyList<float> queryVector, int n, CancellationToken cancellationToken)
    {
        // Vector's constructor takes a ReadOnlyMemory<float>; a float[] converts implicitly, an IReadOnlyList<float>
        // does not, so it is materialized explicitly -- mirroring NewsEmbeddingService's own construction (gh#377).
        Vector query = new(queryVector.ToArray());

        // OrderBy(CosineDistance).Take(n) is what the HNSW cosine index accelerates. CosineDistance is an
        // EF-translated pgvector operator (<=>), not an in-process call, so it exists only on the relational
        // provider -- the reason this whole type is integration-tier (gh#852; the real read is proven by QA #855).
        return await _database.Embeddings
            .Where(embedding => embedding.OwnerKind == EmbeddingOwnerKind.SoftSignal)
            .OrderBy(embedding => embedding.Embedding.CosineDistance(query))
            .Take(n)
            .Select(embedding => new SemanticNeighbor(embedding.OwnerId, embedding.Embedding.CosineDistance(query)))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredEmbedding>> GetVectorsAsync(
        IReadOnlyCollection<string> ownerIds, CancellationToken cancellationToken) =>
        ReadVectorsAsync(EmbeddingOwnerKind.SoftSignal, ownerIds, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredEmbedding>> GetTopicVectorsAsync(
        IReadOnlyCollection<string> topicNames, CancellationToken cancellationToken) =>
        ReadVectorsAsync(EmbeddingOwnerKind.Topic, topicNames, cancellationToken);

    // A plain relational by-owner read of one owner kind's rows for the requested owners -- no CosineDistance, no
    // ordering (gh#853, gh#854). The owner-kind predicate leads, matching the store's (OwnerKind, ...) index. The
    // RANKING is done in-process by the pure EmbeddingSimilarity helper, so the caller scores its KNOWN candidates
    // rather than searching for a global nearest set that might miss a recent-but-near item. Relational-only (the
    // Vector column has no in-memory provider mapping, gh#109), so the read itself is proven by the paired QA card
    // while the ranking is unit-tested. One helper serves both the SoftSignal (news) and Topic reads; the
    // EmbeddingOwnerKind enum stays inside this Api-tier impl and never leaks into the Domain seam.
    private async Task<IReadOnlyList<StoredEmbedding>> ReadVectorsAsync(
        EmbeddingOwnerKind ownerKind, IReadOnlyCollection<string> ownerIds, CancellationToken cancellationToken)
    {
        List<EmbeddingRecord> rows = await _database.Embeddings
            .Where(embedding => embedding.OwnerKind == ownerKind)
            .Where(embedding => ownerIds.Contains(embedding.OwnerId))
            .ToListAsync(cancellationToken);

        // Vector -> plain float[] happens in memory: Pgvector.Vector.ToArray is a client call, not translatable, and
        // keeping the seam's contract a plain float list leaves the domain-side ranking Pgvector-free.
        return [.. rows.Select(embedding => new StoredEmbedding(embedding.OwnerId, embedding.Embedding.ToArray()))];
    }
}
