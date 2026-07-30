using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The Cohere adapter behind the gh#109 <see cref="IEmbeddingProvider"/> seam (gh#403, engineering §2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Degrade, never throw.</b> Retrieval is a hybrid dense + sparse design, so a dense embedding that cannot be
/// produced — rate limit, outage, a bad response — returns <see langword="null"/> and lets retrieval fall back
/// to sparse. It is not on the trading path, and a thrown rate limit on a retrieval call is a worse outcome than
/// slightly worse search results. Only a genuine caller cancellation propagates, so host shutdown stays clean.
/// </para>
/// <para>
/// <b>Meter every call.</b> Cost is the operator's own (ADR-0015), billed to their own key, so an unmetered
/// embed is spend they cannot see. Every path — success, rate limit, failure — records tokens, estimated cost
/// and latency before returning, and the degrade is visible in metrics and logs rather than inferred from worse
/// results.
/// </para>
/// </remarks>
public sealed class CohereEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>The named <see cref="HttpClient"/> this provider uses.</summary>
    public const string HttpClientName = "cohere";

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CohereOptions _options;
    private readonly VectorStore _vectorStore;
    private readonly IEmbeddingMetrics _metrics;
    private readonly ILogger<CohereEmbeddingProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="httpClientFactory">Factory for the named Cohere client.</param>
    /// <param name="options">Cohere configuration.</param>
    /// <param name="vectorStore">Whether this deployment can actually store a vector (gh#474).</param>
    /// <param name="metrics">Spend metering — required, never optional (an unmetered call is invisible spend).</param>
    /// <param name="logger">The logger. The key is never written to it.</param>
    public CohereEmbeddingProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<CohereOptions> options,
        VectorStore vectorStore,
        IEmbeddingMetrics metrics,
        ILogger<CohereEmbeddingProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _vectorStore = vectorStore;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Model => _options.Model;

    /// <inheritdoc />
    public int Dimensions => TradingCopilotDbContext.EmbeddingDimensions;

    /// <inheritdoc />
    /// <remarks>
    /// Both halves, deliberately (gh#474): a key to make the call, and somewhere to put what comes back. A
    /// configured key over a Postgres without <c>pgvector</c> is spend with no product -- the embed succeeds and
    /// the upsert faults, every poll, forever.
    /// </remarks>
    public bool IsAvailable => _options.IsConfigured && _vectorStore.IsPresent;

    /// <inheritdoc />
    public async Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            // the keyless default should be registered instead, but never reach the network regardless
            return new EmbeddingResult(null, EmbeddingOutcome.Failed, 0, 0m);
        }

        long startedTicks = Stopwatch.GetTimestamp();
        EmbeddingOutcome outcome = EmbeddingOutcome.Failed;
        int billedTokens = 0;
        decimal estimatedCostUsd = 0m;

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl), "/v2/embed"))
            {
                Headers = { Authorization = new("Bearer", _options.ApiKey) },
                Content = JsonContent.Create(new CohereEmbedRequest(
                    _options.Model, [text], "search_document", ["float"])),
            };

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                outcome = EmbeddingOutcome.RateLimited;
                _logger.LogWarning("Cohere rate-limited the embed request — retrieval degrades to sparse for this call.");
                return new EmbeddingResult(null, outcome, billedTokens, estimatedCostUsd);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Cohere embed returned {Status} — retrieval degrades to sparse for this call.", (int)response.StatusCode);
                return new EmbeddingResult(null, outcome, billedTokens, estimatedCostUsd);
            }

            CohereEmbedResponse? body = await response.Content.ReadFromJsonAsync<CohereEmbedResponse>(_jsonOptions, cancellationToken);
            IReadOnlyList<float>? vector = body?.Embeddings?.Float?.FirstOrDefault();

            if (vector is null || vector.Count != Dimensions)
            {
                _logger.LogWarning(
                    "Cohere embed response carried no usable {Dimensions}-dim vector — retrieval degrades to sparse.", Dimensions);
                return new EmbeddingResult(null, outcome, billedTokens, estimatedCostUsd);
            }

            billedTokens = body!.Meta?.BilledUnits?.InputTokens ?? 0;
            estimatedCostUsd = _options.EstimateCost(billedTokens);
            outcome = EmbeddingOutcome.Embedded;
            return new EmbeddingResult(vector, outcome, billedTokens, estimatedCostUsd);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine shutdown, not a provider fault to swallow
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            // Timeouts surface as an OperationCanceledException whose token is NOT the caller's, so they land here.
            _logger.LogWarning(error, "Cohere embed failed — retrieval degrades to sparse for this call.");
            return new EmbeddingResult(null, outcome, billedTokens, estimatedCostUsd);
        }
        finally
        {
            TimeSpan latency = Stopwatch.GetElapsedTime(startedTicks);
            // The SAME billedTokens/estimatedCostUsd that ride the returned EmbeddingResult are what get metered
            // here -- computed once above, so the two sinks (Prometheus and the caller's AIUsage row, gh#377) can
            // never disagree.
            _metrics.RecordEmbed(_options.Model, outcome, billedTokens, estimatedCostUsd, latency);
        }
    }

    private sealed record CohereEmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("texts")] IReadOnlyList<string> Texts,
        [property: JsonPropertyName("input_type")] string InputType,
        [property: JsonPropertyName("embedding_types")] IReadOnlyList<string> EmbeddingTypes);

    private sealed record CohereEmbedResponse(
        [property: JsonPropertyName("embeddings")] CohereEmbeddings? Embeddings,
        [property: JsonPropertyName("meta")] CohereMeta? Meta);

    private sealed record CohereEmbeddings(
        [property: JsonPropertyName("float")] IReadOnlyList<IReadOnlyList<float>>? Float);

    private sealed record CohereMeta(
        [property: JsonPropertyName("billed_units")] CohereBilledUnits? BilledUnits);

    private sealed record CohereBilledUnits(
        [property: JsonPropertyName("input_tokens")] int InputTokens);
}
