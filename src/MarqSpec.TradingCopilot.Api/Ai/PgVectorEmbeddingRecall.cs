using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The production <see cref="IEmbeddingRecall"/> (gh#1065, generalising gh#852): the ranked nearest-neighbour read over
/// the scoped <see cref="TradingCopilotDbContext"/>, ordered by <c>pgvector</c> cosine distance to match the HNSW
/// cosine indexes the <see cref="EmbeddingRecord"/> store is built on (data dictionary §10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Relational-only, by design (gh#109).</b> <see cref="EmbeddingRecord"/>'s <c>Vector</c> column has no
/// in-memory-provider mapping — <see cref="TradingCopilotDbContext"/> <c>Ignore()</c>s the entity off Postgres — so the
/// <c>CosineDistance</c> operator these queries order by exists only on the relational provider. Coverage is therefore
/// integration-tier, which is exactly why the consumer (<see cref="IContextRetrievalService"/>) depends on the
/// <see cref="IEmbeddingRecall"/> seam and not on this type: the pipeline's decision logic stays unit-testable against a
/// fake while the real read is proven against real Postgres.
/// </para>
/// <para>
/// <b>Why one query per kind rather than one parameterised query.</b> Each kind's read must stay matchable by its own
/// <b>partial</b> HNSW index (<c>IX_Embeddings_Vector_Cosine_SoftSignal</c> from gh#864, and the
/// <c>…_Suggestion</c> / <c>…_JournalEntry</c> pair from gh#1065). A partial index is matched by comparing the query's
/// predicate to the index's, and that comparison needs a <b>constant</b>: writing
/// <c>.Where(e =&gt; e.OwnerKind == ownerKind)</c> over a method parameter makes EF emit a SQL <i>parameter</i>, which
/// no partial index predicate can be proven to cover. Postgres would then serve the <c>ORDER BY … LIMIT</c> from the
/// table-wide HNSW graph and apply the owner kind as a post-scan filter — and a crowd of closer other-kind rows fills
/// the approximate candidate window, so the read returns <b>zero</b> neighbours though thousands exist (gh#861
/// reproduced this at 15,000 rows). So the dispatch below is a <c>switch</c> onto per-kind methods, each carrying its
/// literal <see cref="EmbeddingOwnerKind"/>, and the shared part is only the projection.
/// </para>
/// <para>
/// <b>Current-model reads (gh#889).</b> Every query also filters <c>Model == provider.Model</c>: vectors from different
/// models are not comparable, so an unfiltered ranked read could rank a retired-model vector or return the same owner
/// twice. During a model change that filter is a <i>second</i> post-scan filter on the HNSW window, so recall can
/// transiently narrow — still distance-ordered, still never wrong — until the embed pass re-embeds and sweeps
/// (see ADR-0001).
/// </para>
/// </remarks>
public sealed class PgVectorEmbeddingRecall : IEmbeddingRecall
{
    private readonly TradingCopilotDbContext _database;
    private readonly IEmbeddingProvider _provider;

    /// <summary>Creates the recall over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="provider">The embedding provider — its <see cref="IEmbeddingProvider.Model"/> scopes every read to the current model's vectors (gh#889).</param>
    public PgVectorEmbeddingRecall(TradingCopilotDbContext database, IEmbeddingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(provider);
        _database = database;
        _provider = provider;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SemanticNeighbor>> NearestAsync(
        RetrievalKind kind, IReadOnlyList<float> queryVector, int n, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queryVector);

        // Vector's constructor takes a ReadOnlyMemory<float>; a float[] converts implicitly, an IReadOnlyList<float>
        // does not, so it is materialized explicitly -- mirroring NewsEmbeddingService's own construction (gh#377).
        Vector query = new(queryVector.ToArray());

        // Each branch carries its owner kind as a LITERAL so the matching partial HNSW index can be planned against
        // it -- see the type remarks. Do not collapse these into one parameterised query.
        return kind switch
        {
            RetrievalKind.News => RankAsync(
                _database.Embeddings.Where(embedding => embedding.OwnerKind == EmbeddingOwnerKind.SoftSignal),
                query, n, cancellationToken),

            RetrievalKind.Suggestion => RankAsync(
                _database.Embeddings.Where(embedding => embedding.OwnerKind == EmbeddingOwnerKind.Suggestion),
                query, n, cancellationToken),

            RetrievalKind.JournalEntry => RankAsync(
                _database.Embeddings.Where(embedding => embedding.OwnerKind == EmbeddingOwnerKind.JournalEntry),
                query, n, cancellationToken),

            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "That retrieval kind has no ranked embedding read."),
        };
    }

    // The shared tail of every kind's query: pin the current model, order by cosine distance, take n, project. The
    // owner-kind predicate is already applied by the caller as a literal, so this composes ON TOP of it rather than
    // parameterising it.
    private async Task<IReadOnlyList<SemanticNeighbor>> RankAsync(
        IQueryable<EmbeddingRecord> ofKind, Vector query, int n, CancellationToken cancellationToken) =>
        await ofKind
            .Where(embedding => embedding.Model == _provider.Model) // current model only (gh#889)
            .OrderBy(embedding => embedding.Embedding.CosineDistance(query))
            .Take(n)
            .Select(embedding => new SemanticNeighbor(embedding.OwnerId, embedding.Embedding.CosineDistance(query)))
            .ToListAsync(cancellationToken);
}
