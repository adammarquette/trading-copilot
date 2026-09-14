using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// An <b>adversarial</b> stand-in for the rerank seam (gh#988, of gh#987), mirroring
/// <see cref="AdversarialEmbeddingProvider"/>: Cohere rerank is an outbound third-party seam that cannot exist
/// pre-merge (no key, no egress), so it is doubled here — never at <see cref="NewsRetrievalService"/>'s own
/// consumption of <c>IReranker.RerankAsync</c>'s result, which stays real production code under test.
/// </summary>
/// <remarks>
/// It <b>reverses</b> the candidate order it is handed — a distinctive, deterministic transform that could never be
/// mistaken for the untouched recall order — so a case using this double can prove the pipeline's returned list
/// genuinely reads the reranker's own order rather than silently falling back to (or re-deriving) the recall order.
/// A production bug that ignored <c>RerankResult.Ranking</c> and returned the hydrated recall list unreordered
/// would leave the recall (ascending-distance) order in place instead of this double's reversed one, and the case
/// built on it would go red.
/// </remarks>
public sealed class AdversarialReranker : IReranker
{
    /// <inheritdoc />
    public string Model => "adversarial-rerank-v1";

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public Task<RerankResult> RerankAsync(
        string query, IReadOnlyList<string> documents, int topN, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);

        // The identity fallback elsewhere in this codebase (UnavailableReranker / CohereRerankProvider's own
        // Passthrough) returns candidates in their ORIGINAL order -- the opposite of what this double must do to be
        // distinguishable from a passthrough. Reversing and re-scoring descending keeps the result shaped like a
        // genuine rerank response (a permutation, scores descending) rather than a degrade.
        List<RankedDocument> reversed = [.. Enumerable.Range(0, documents.Count)
            .Reverse()
            .Take(Math.Clamp(topN, 0, documents.Count))
            .Select((originalIndex, rank) => new RankedDocument(originalIndex, 1.0 - (rank * 0.01)))];

        return Task.FromResult(new RerankResult(reversed, RerankOutcome.Reranked, documents.Count, 0m));
    }
}
