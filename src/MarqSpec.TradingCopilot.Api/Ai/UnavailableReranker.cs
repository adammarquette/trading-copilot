using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The default <see cref="IReranker"/> (gh#975): reports itself unavailable and passes the candidates through in their
/// original order, never reaching the network. It takes no <see cref="HttpClient"/> at all, so it <i>cannot</i> spend.
/// </summary>
/// <remarks>
/// <para>
/// <b>The substrate must be usable with no API key and no spend</b>, so this — not the Cohere adapter — is what is
/// registered until a real rerank provider is configured. Every downstream test therefore runs without external
/// credentials, the keyless-default counterpart of <see cref="UnavailableEmbeddingProvider"/>.
/// </para>
/// <para>
/// <b>Passthrough, not an empty list.</b> Rerank is a reordering refinement over a recall the consumer already holds;
/// with no provider the first-stage order is a correct — if unsharpened — answer, so the identity order is returned
/// rather than dropping the candidates. It says so once, at error, on first use: a deployment with reranking quietly
/// switched off is a configuration state to announce, not a per-call log flood.
/// </para>
/// </remarks>
public sealed class UnavailableReranker : IReranker
{
    private readonly ILogger<UnavailableReranker> _logger;
    private int _announced;

    /// <summary>Creates the reranker.</summary>
    /// <param name="logger">The logger.</param>
    public UnavailableReranker(ILogger<UnavailableReranker> logger) => _logger = logger;

    /// <inheritdoc />
    public string Model => "none";

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task<RerankResult> RerankAsync(
        string query, IReadOnlyList<string> documents, int topN, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);

        // Once, not per call: a retrieval loop would otherwise turn a configuration state into a log flood, and a
        // flooding error is one that gets filtered out.
        if (Interlocked.Exchange(ref _announced, 1) == 0)
        {
            _logger.LogError(
                "No rerank provider is configured, so retrieval reranking is OFF (gh#975). Configure the Cohere rerank "
                + "provider to enable it. Retrieval keeps its first-stage order; nothing on the trading path depends on this.");
        }

        IReadOnlyList<RankedDocument> identity = Enumerable.Range(0, Math.Clamp(topN, 0, documents.Count))
            .Select(index => new RankedDocument(index, 0d))
            .ToList();

        return Task.FromResult(new RerankResult(identity, RerankOutcome.Failed, 0, 0m));
    }
}
