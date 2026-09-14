namespace MarqSpec.TradingCopilot.Domain.Ai;

/// <summary>
/// Reranks candidate documents against a query (gh#975, engineering §2) — the seam a real provider (Cohere rerank)
/// implements and a retrieval consumer depends on instead of an SDK. It is the cross-encoder second pass that
/// sharpens the order of a first-stage (embedding / lexical) recall, mirroring <see cref="IEmbeddingProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unavailable or a fault is a first-class DEGRADED answer, not an exception</b> — the same posture as
/// <see cref="IEmbeddingProvider"/>. Rerank rides an external, rate-limited, paid API; any of those can be absent on
/// a perfectly healthy deployment, so the seam <b>degrades to passthrough</b>: it returns the candidates in their
/// <i>original</i> order (identity) rather than throwing, and a caller keeps its first-stage order instead of taking
/// an exception on a retrieval path. Only a genuine caller cancellation propagates, so host shutdown stays clean.
/// </para>
/// <para>
/// <b>Why identity order rather than an error or an empty list.</b> Rerank is a <i>reordering</i> refinement over a
/// recall the consumer already holds; if it cannot run, the first-stage order is a correct — if unsharpened — answer.
/// Dropping the candidates (empty) or throwing would turn a soft quality degrade into a hard retrieval failure, the
/// worse outcome — the same reasoning that makes an unavailable embed a <see langword="null"/> vector, not a zero one.
/// </para>
/// <para>
/// <b>The result carries spend facts, not just the order</b> (mirroring <see cref="EmbeddingResult"/>). Cohere
/// rerank bills per <i>search</i> — one call is one search unit — so <see cref="RerankResult.Outcome"/>,
/// <see cref="RerankResult.BilledSearches"/> and <see cref="RerankResult.EstimatedCostUsd"/> are priced <b>at the
/// provider</b> — the same values <see cref="IRerankMetrics"/> meters — and ride back so a scoped consumer can price
/// and record the call without knowing which concrete provider (or rate) is behind the seam.
/// </para>
/// </remarks>
public interface IReranker
{
    /// <summary>The rerank model identifier — different models rank differently, so it is worth recording alongside a call.</summary>
    string Model { get; }

    /// <summary>
    /// Whether reranking can currently be performed. <see langword="false"/> is an ordinary state — no API key, a
    /// provider deliberately disabled — and callers may branch on it, though a call is safe regardless (it degrades
    /// to passthrough rather than throwing).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Reranks <paramref name="documents"/> against <paramref name="query"/>, most-relevant first, keeping at most
    /// <paramref name="topN"/> of them.
    /// </summary>
    /// <param name="query">The query the documents are ranked against.</param>
    /// <param name="documents">The first-stage candidate documents, in their original (recall) order.</param>
    /// <param name="topN">
    /// The maximum number of ranked results to return; a value at or above the candidate count returns them all, and
    /// a value at or below zero returns none.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The ranked result. On any degrade (unavailable, rate limit, transport fault, or a bad response) the ranking is
    /// the identity order — the first <paramref name="topN"/> candidates in their original positions — never a throw
    /// and never a dropped candidate set.
    /// </returns>
    Task<RerankResult> RerankAsync(
        string query, IReadOnlyList<string> documents, int topN, CancellationToken cancellationToken);
}

/// <summary>
/// The result of one rerank call (gh#975) — the ranked view of the candidates a retrieval caller wants, plus the
/// spend facts a scoped consumer needs to ledger the call (mirroring <see cref="EmbeddingResult"/>) without reaching
/// into a concrete provider's pricing.
/// </summary>
/// <param name="Ranking">
/// The candidates in ranked order (most relevant first), each pointing back at its position in the input list. On a
/// degrade this is the identity order — the input's own order, truncated to the requested top-n.
/// </param>
/// <param name="Outcome">How the call ended — the same dimension <see cref="IRerankMetrics"/> meters.</param>
/// <param name="BilledSearches">Search units the provider billed; zero for a degraded or unattempted call.</param>
/// <param name="EstimatedCostUsd">
/// The dollar cost of <paramref name="BilledSearches"/>, priced at the provider's own pinned rate; zero when none
/// were billed.
/// </param>
public sealed record RerankResult(
    IReadOnlyList<RankedDocument> Ranking, RerankOutcome Outcome, int BilledSearches, decimal EstimatedCostUsd);

/// <summary>
/// One ranked candidate (gh#975): where it sat in the input, and how relevant the provider judged it.
/// </summary>
/// <remarks>
/// On a passthrough degrade the <see cref="RelevanceScore"/> is not meaningful (the provider never scored it) and the
/// order is the input order — a consumer reads the <b>list order</b>, which is authoritative on both the reranked and
/// the degraded path, rather than re-sorting by a score a degrade did not produce.
/// </remarks>
/// <param name="Index">The candidate's zero-based position in the input <c>documents</c> list.</param>
/// <param name="RelevanceScore">The provider's relevance score (higher is more relevant); zero on a degrade.</param>
public sealed record RankedDocument(int Index, double RelevanceScore);
