using System.Linq.Expressions;
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
/// reproduced this at 15,000 rows). So the owner-kind predicate comes from <see cref="PredicateFor"/>, a
/// <c>switch</c> onto one pre-built expression per kind, each carrying its <b>literal</b>
/// <see cref="EmbeddingOwnerKind"/>; everything after it composes on top. Being an expression rather than three
/// inline lambdas is what lets a unit test compile the shipping predicate and assert the mapping, which is the only
/// tier that can — a fake seam skips the switch, and the query itself needs a database.
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

    /// <summary>
    /// The owner-kind predicate this read sends for <paramref name="kind"/> — the <b>one</b> place the ranked path
    /// maps a retrieval kind onto a stored owner kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public, and an <see cref="Expression{TDelegate}"/> rather than an inline lambda, so the predicate that actually
    /// ships can be <b>compiled and asserted</b> by a unit test against <see cref="EmbeddingOwnerKinds.For"/>. That
    /// binding is the point: three branches differing by a single token, over a read whose only symptom of a slipped
    /// token is a <i>silently empty</i> result forever — nothing throws, nothing logs, and the kind's partial index is
    /// simply never planned against. Faking <see cref="IEmbeddingRecall"/> (as every consumer's unit test does) cannot
    /// reach this switch, and a database is needed to reach the query, so exposing the expression is what puts the
    /// mapping under test at the unit tier.
    /// </para>
    /// <para>
    /// Each branch still carries its owner kind as a <b>literal</b>. The expression tree holds a constant exactly as an
    /// inline lambda would, so EF renders a SQL literal rather than a parameter and the partial index stays plannable
    /// — see the type remarks for why that is load-bearing.
    /// </para>
    /// </remarks>
    /// <param name="kind">The retrieval kind whose stored rows to select.</param>
    /// <returns>A predicate selecting exactly that kind's rows.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind has no ranked read.</exception>
    public static Expression<Func<EmbeddingRecord, bool>> PredicateFor(RetrievalKind kind) => kind switch
    {
        RetrievalKind.News => embedding => embedding.OwnerKind == EmbeddingOwnerKind.SoftSignal,
        RetrievalKind.Suggestion => embedding => embedding.OwnerKind == EmbeddingOwnerKind.Suggestion,
        RetrievalKind.JournalEntry => embedding => embedding.OwnerKind == EmbeddingOwnerKind.JournalEntry,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "That retrieval kind has no ranked embedding read."),
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<SemanticNeighbor>> NearestAsync(
        RetrievalKind kind, IReadOnlyList<float> queryVector, int n, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queryVector);

        // Resolved BEFORE the vector is built, so an unmappable kind is refused without any wasted work.
        Expression<Func<EmbeddingRecord, bool>> ofKind = PredicateFor(kind);

        // Vector's constructor takes a ReadOnlyMemory<float>; a float[] converts implicitly, an IReadOnlyList<float>
        // does not, so it is materialized explicitly -- mirroring NewsEmbeddingService's own construction (gh#377).
        Vector query = new(queryVector.ToArray());

        // The owner-kind predicate leads and carries a LITERAL (see PredicateFor); the model filter composes on top,
        // then the distance ordering the partial HNSW index accelerates.
        return await _database.Embeddings
            .Where(ofKind)
            .Where(embedding => embedding.Model == _provider.Model) // current model only (gh#889)
            .OrderBy(embedding => embedding.Embedding.CosineDistance(query))
            .Take(n)
            .Select(embedding => new SemanticNeighbor(embedding.OwnerId, embedding.Embedding.CosineDistance(query)))
            .ToListAsync(cancellationToken);
    }
}
