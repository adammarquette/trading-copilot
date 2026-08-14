using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Turns a retrieval query into its nearest stored news (gh#852, R-2): embed the query, then read the similarity
/// seam — and carry the whole graceful-degrade decision so the caller (a news feed) gets a semantic axis when one
/// is available and simply falls back to its non-semantic ranking when one is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Degrade, never throw</b> — the gh#109 posture, one layer up from the provider. Three independent things can
/// leave semantic retrieval off on a perfectly healthy deployment: no embedding provider configured, a query that
/// fails to embed (rate limit / outage → a null vector), or a <c>pgvector</c> read that faults. Each yields an
/// empty result so the feed keeps working; none surfaces as an error. Only a genuine caller cancellation
/// propagates, so host shutdown stays clean — mirroring <see cref="CohereEmbeddingProvider"/>'s own degrade catch.
/// </para>
/// <para>
/// <b>No spend when unavailable.</b> An unavailable provider short-circuits <i>before</i> the embed call, so a
/// deployment with semantic retrieval off never makes — and pays for — a query embedding it would only discard.
/// </para>
/// <para>
/// <b>Scoped.</b> It holds the <see cref="INewsEmbeddingSimilarity"/> seam, whose production implementation carries
/// the scoped <c>TradingCopilotDbContext</c>, so this is a scoped service too.
/// </para>
/// </remarks>
public sealed class NewsSemanticSearch
{
    private readonly IEmbeddingProvider _provider;
    private readonly INewsEmbeddingSimilarity _similarity;
    private readonly ILogger<NewsSemanticSearch> _logger;

    /// <summary>Creates the search over the embedding provider and the similarity seam.</summary>
    /// <param name="provider">The embedding provider — Cohere when configured, the keyless no-op default otherwise.</param>
    /// <param name="similarity">The nearest-news seam — the real pgvector read in production, a fake in unit tests.</param>
    /// <param name="logger">The logger.</param>
    public NewsSemanticSearch(
        IEmbeddingProvider provider,
        INewsEmbeddingSimilarity similarity,
        ILogger<NewsSemanticSearch> logger)
    {
        _provider = provider;
        _similarity = similarity;
        _logger = logger;
    }

    /// <summary>Finds the <paramref name="n"/> stored news items nearest to <paramref name="queryText"/>.</summary>
    /// <param name="queryText">The retrieval query to embed and match against the stored news embeddings.</param>
    /// <param name="n">How many nearest neighbours to return.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The nearest news owners nearest-first, or an empty list when semantic retrieval is unavailable or faulted.</returns>
    public async Task<IReadOnlyList<SemanticNeighbor>> NearestNewsForQueryAsync(
        string queryText, int n, CancellationToken cancellationToken)
    {
        if (!_provider.IsAvailable)
        {
            // No provider: the semantic axis is simply off (gh#109). No embed (no paid call) and no seam read -- the
            // caller falls back to its non-semantic ranking.
            return [];
        }

        EmbeddingResult query = await _provider.EmbedQueryAsync(queryText, cancellationToken);
        if (query.Vector is null)
        {
            // The query could not be embedded (rate limit, outage, unavailable mid-flight). A null vector is the
            // provider's honest "no answer" -- searching with it would rank every row identically, so stop here.
            return [];
        }

        try
        {
            return await _similarity.NearestNewsAsync(query.Vector, n, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine shutdown, not a read fault to swallow
        }
        catch (Exception error)
        {
            // The read is off the trading path: a pgvector outage (or any query fault) degrades the feed to its
            // non-semantic axis rather than erroring.
            _logger.LogWarning(
                error, "Semantic news search failed; the feed degrades to its non-semantic axis for this query.");
            return [];
        }
    }
}
