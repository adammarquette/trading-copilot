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
}
