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

    /// <summary>
    /// Reads back the stored soft-signal embeddings for the given owners (gh#853) — the salience read that lets the
    /// feed score its window candidates against the operator's starred items in-process.
    /// </summary>
    /// <remarks>
    /// A plain by-owner read over the same <c>SoftSignal</c> embeddings <see cref="NearestNewsAsync"/> ranks — no
    /// ordering, no distance operator — returning each requested owner's vector as a plain <see cref="float"/> list so
    /// the seam stays <c>Pgvector</c>- and <c>Data</c>-free. The <b>ranking</b> is a pure, unit-tested function over
    /// these vectors (<c>EmbeddingSimilarity.MaxCosineSimilarity</c>, with <c>Domain.Signals</c> unaware of it), so
    /// only the read itself is integration-tier. This replaces a nearest-N search: the feed already knows its
    /// candidate window, so it <i>scores those candidates</i> rather than searching for a global nearest set that
    /// might miss them. Owners with no stored embedding are simply absent from the result; an unavailable or faulting
    /// store yields an empty list (the gh#109 degrade), never a throw — only a genuine caller cancellation propagates.
    /// </remarks>
    /// <param name="ownerIds">The owners whose stored vectors to read (a starred set or a candidate window).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The stored embedding of each requested owner that has one; empty when retrieval cannot run.</returns>
    Task<IReadOnlyList<StoredEmbedding>> GetVectorsAsync(
        IReadOnlyCollection<string> ownerIds, CancellationToken cancellationToken);

    /// <summary>
    /// Reads back the stored <b>topic</b> embeddings for the given topic names (gh#854) — the semantic-topic-match read
    /// that lets the relevance pass compare a news item's vector to the deployment's topic vectors in-process.
    /// </summary>
    /// <remarks>
    /// The topic analogue of <see cref="GetVectorsAsync"/>: a plain by-owner read over the <c>Topic</c> embeddings (a
    /// topic's name + keywords), returning each requested topic's vector as a plain <see cref="float"/> list. Same
    /// posture — a topic with no stored embedding is simply absent, an unavailable or faulting store yields an empty
    /// list (the gh#109 degrade) rather than a throw, and only a genuine caller cancellation propagates. Kept a
    /// distinct, semantically-named read rather than an owner-kind parameter, so the Domain seam carries no <c>Data</c>
    /// <c>EmbeddingOwnerKind</c> dependency.
    /// </remarks>
    /// <param name="topicNames">The topic names whose stored vectors to read.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The stored embedding of each requested topic that has one; empty when retrieval cannot run.</returns>
    Task<IReadOnlyList<StoredEmbedding>> GetTopicVectorsAsync(
        IReadOnlyCollection<string> topicNames, CancellationToken cancellationToken);
}

/// <summary>
/// One nearest-neighbour hit (gh#852): an embedded owner and its cosine distance from the query. For a news
/// embedding the <see cref="OwnerId"/> is the <c>NewsRecord</c> dedup key (data dictionary §10).
/// </summary>
/// <param name="OwnerId">The embedded owner's id — a <c>NewsRecord.DedupKey</c> for a soft-signal embedding.</param>
/// <param name="Distance">The cosine distance in <c>[0, 2]</c>; smaller is nearer, and similarity is <c>1 - Distance</c>.</param>
public sealed record SemanticNeighbor(string OwnerId, double Distance);

/// <summary>
/// One stored embedding read back by owner (gh#853): the embedded owner's id and its vector as a plain
/// <see cref="float"/> list, so the read seam carries no <c>Pgvector</c> dependency into the domain.
/// </summary>
/// <param name="OwnerId">The embedded owner's id — a <c>NewsRecord.DedupKey</c> for a soft-signal embedding.</param>
/// <param name="Vector">The stored embedding as a plain float list.</param>
public sealed record StoredEmbedding(string OwnerId, IReadOnlyList<float> Vector);
