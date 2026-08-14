namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>
/// The nearest-news similarity read seam (gh#852, R-2) over the stored soft-signal embeddings — the boundary that
/// keeps the surrounding retrieval logic unit-testable while the real <c>pgvector</c> <c>CosineDistance</c> read
/// stays integration-tier (data dictionary §10; proven by QA #855).
/// </summary>
/// <remarks>
/// <para>
/// <b>Cosine distance, not similarity.</b> Each <see cref="SemanticNeighbor.Distance"/> is a cosine distance in
/// <c>[0, 2]</c> — the metric the <c>EmbeddingRecord</c> store's HNSW cosine index is built on — so smaller is
/// nearer and similarity is <c>1 - Distance</c>. Ascending by distance is the nearest-first ranking a caller wants.
/// </para>
/// <para>
/// <b>Unavailable or faulting is an empty result, never a throw.</b> This read is off the trading path: pgvector
/// may be absent (the gh#109 degrade), no provider may be configured, or the query may fault — and every one of
/// those yields no neighbours, so the caller falls back to its non-semantic axis exactly as the embedding provider
/// returns a null vector rather than throwing. Only a genuine caller cancellation propagates. Pure Domain — the
/// query is a plain <see cref="float"/> list, so the seam carries no <c>Pgvector</c> or <c>Data</c> dependency.
/// </para>
/// </remarks>
public interface INewsEmbeddingSimilarity
{
    /// <summary>Finds the <paramref name="n"/> stored news embeddings nearest to <paramref name="queryVector"/>.</summary>
    /// <param name="queryVector">The query embedding, as a plain float list so the seam stays <c>Pgvector</c>-free.</param>
    /// <param name="n">How many nearest neighbours to return.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The nearest news owners and their cosine distance, nearest first; empty when retrieval cannot run.</returns>
    Task<IReadOnlyList<SemanticNeighbor>> NearestNewsAsync(
        IReadOnlyList<float> queryVector, int n, CancellationToken cancellationToken);
}

/// <summary>
/// One nearest-neighbour hit (gh#852): an embedded owner and its cosine distance from the query. For a news
/// embedding the <see cref="OwnerId"/> is the <c>NewsRecord</c> dedup key (data dictionary §10).
/// </summary>
/// <param name="OwnerId">The embedded owner's id — a <c>NewsRecord.DedupKey</c> for a soft-signal embedding.</param>
/// <param name="Distance">The cosine distance in <c>[0, 2]</c>; smaller is nearer, and similarity is <c>1 - Distance</c>.</param>
public sealed record SemanticNeighbor(string OwnerId, double Distance);
