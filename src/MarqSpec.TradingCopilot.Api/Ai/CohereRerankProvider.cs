using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The Cohere adapter behind the gh#975 <see cref="IReranker"/> seam — the cross-encoder rerank counterpart of
/// <see cref="CohereEmbeddingProvider"/>, over the <b>same</b> named <c>cohere</c> <see cref="HttpClient"/> and key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Degrade, never throw.</b> Rerank sharpens the order of a first-stage recall the consumer already holds, so a
/// ranking that cannot be produced — rate limit, outage, a bad response — returns the candidates in their
/// <i>original</i> order (a passthrough) rather than throwing. It is not on the trading path, and a thrown rate limit
/// on a retrieval call is a worse outcome than a slightly less-sharp order. Only a genuine caller cancellation
/// propagates, so host shutdown stays clean.
/// </para>
/// <para>
/// <b>Meter every call.</b> Cost is the operator's own (ADR-0015), billed to their own key, so an unmetered rerank is
/// spend they cannot see. Every path that reaches the provider — success, rate limit, failure — records search units,
/// estimated cost and latency before returning, and the degrade is visible in metrics and logs rather than inferred.
/// </para>
/// </remarks>
public sealed class CohereRerankProvider : IReranker
{
    /// <summary>The named <see cref="HttpClient"/> this provider uses — the one the embed adapter already registers.</summary>
    public const string HttpClientName = CohereEmbeddingProvider.HttpClientName;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CohereOptions _options;
    private readonly IRerankMetrics _metrics;
    private readonly ILogger<CohereRerankProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="httpClientFactory">Factory for the named Cohere client.</param>
    /// <param name="options">Cohere configuration.</param>
    /// <param name="metrics">Spend metering — required, never optional (an unmetered call is invisible spend).</param>
    /// <param name="logger">The logger. The key is never written to it.</param>
    public CohereRerankProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<CohereOptions> options,
        IRerankMetrics metrics,
        ILogger<CohereRerankProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Model => _options.RerankModel;

    /// <inheritdoc />
    public bool IsAvailable => _options.IsConfigured;

    /// <inheritdoc />
    public async Task<RerankResult> RerankAsync(
        string query, IReadOnlyList<string> documents, int topN, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (!_options.IsConfigured)
        {
            // the keyless default should be registered instead, but never reach the network regardless
            return new RerankResult(Passthrough(documents.Count, topN), RerankOutcome.Failed, 0, 0m);
        }

        if (documents.Count == 0 || topN <= 0)
        {
            // Nothing to rank (or nothing wanted): a passthrough of the empty / zero set that never reaches the
            // network — so, like the unconfigured guard above, it is not a provider call and is not metered.
            return new RerankResult(Passthrough(documents.Count, topN), RerankOutcome.Reranked, 0, 0m);
        }

        long startedTicks = Stopwatch.GetTimestamp();
        RerankOutcome outcome = RerankOutcome.Failed;
        int billedSearches = 0;
        decimal estimatedCostUsd = 0m;

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl), "/v2/rerank"))
            {
                Headers = { Authorization = new("Bearer", _options.ApiKey) },
                Content = JsonContent.Create(new CohereRerankRequest(_options.RerankModel, query, documents, topN)),
            };

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                outcome = RerankOutcome.RateLimited;
                _logger.LogWarning("Cohere rate-limited the rerank request — ranking degrades to passthrough for this call.");
                return new RerankResult(Passthrough(documents.Count, topN), outcome, billedSearches, estimatedCostUsd);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Cohere rerank returned {Status} — ranking degrades to passthrough for this call.", (int)response.StatusCode);
                return new RerankResult(Passthrough(documents.Count, topN), outcome, billedSearches, estimatedCostUsd);
            }

            CohereRerankResponse? body = await response.Content.ReadFromJsonAsync<CohereRerankResponse>(_jsonOptions, cancellationToken);

            if (body?.Results is null)
            {
                _logger.LogWarning("Cohere rerank response carried no results — ranking degrades to passthrough.");
                return new RerankResult(Passthrough(documents.Count, topN), outcome, billedSearches, estimatedCostUsd);
            }

            // Map the wire results to input-relative ranked docs, dropping any out-of-range index defensively (an
            // index outside the candidate set is a bad shape for THAT entry, not a reason to fail the whole call).
            IReadOnlyList<RankedDocument> ranking = body.Results
                .Where(result => result.Index >= 0 && result.Index < documents.Count)
                .Select(result => new RankedDocument(result.Index, result.RelevanceScore))
                .ToList();

            // A successful rerank consumed at least one search unit; default to 1 when the provider omits the block,
            // rather than under-count a call that plainly happened.
            billedSearches = body.Meta?.BilledUnits?.SearchUnits ?? 1;
            estimatedCostUsd = _options.EstimateRerankCost(billedSearches);
            outcome = RerankOutcome.Reranked;
            return new RerankResult(ranking, outcome, billedSearches, estimatedCostUsd);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine shutdown, not a provider fault to swallow
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            // Timeouts surface as an OperationCanceledException whose token is NOT the caller's, so they land here.
            _logger.LogWarning(error, "Cohere rerank failed — ranking degrades to passthrough for this call.");
            return new RerankResult(Passthrough(documents.Count, topN), outcome, billedSearches, estimatedCostUsd);
        }
        finally
        {
            TimeSpan latency = Stopwatch.GetElapsedTime(startedTicks);
            // The SAME billedSearches / estimatedCostUsd that ride the returned RerankResult are metered here —
            // computed once above, so the two sinks (Prometheus and a future consumer's AIUsage row) never disagree.
            _metrics.RecordRerank(_options.RerankModel, outcome, billedSearches, estimatedCostUsd, latency);
        }
    }

    // The identity fallback: the first top-n candidates in their original positions. A degrade returns the recall's
    // own order (a correct, if unsharpened, answer) rather than dropping candidates or throwing; the score is not
    // meaningful on this path, so it is zero, and a consumer reads the list order.
    private static IReadOnlyList<RankedDocument> Passthrough(int documentCount, int topN) =>
        Enumerable.Range(0, Math.Clamp(topN, 0, documentCount))
            .Select(index => new RankedDocument(index, 0d))
            .ToList();

    private sealed record CohereRerankRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents,
        [property: JsonPropertyName("top_n")] int TopN);

    private sealed record CohereRerankResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<CohereRerankResult>? Results,
        [property: JsonPropertyName("meta")] CohereRerankMeta? Meta);

    private sealed record CohereRerankResult(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("relevance_score")] double RelevanceScore);

    private sealed record CohereRerankMeta(
        [property: JsonPropertyName("billed_units")] CohereRerankBilledUnits? BilledUnits);

    private sealed record CohereRerankBilledUnits(
        [property: JsonPropertyName("search_units")] int SearchUnits);
}
