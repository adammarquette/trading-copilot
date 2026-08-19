using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Chat.Tools;

/// <summary>
/// The <c>search_news</c> chat tool (gh#987, R-6) — a read-only <b>semantic</b> search over the operator's ingested
/// news / soft-signal feed. Since gh#995 it is a <b>thin adapter</b> over the shared
/// <see cref="INewsRetrievalService"/> pipeline (embed the query → nearest-news recall → hydrate → rerank): the tool
/// parses the model's JSON input, calls the pipeline, and serialises its items into the compact result shape the
/// model reads. The retrieval logic — and its degrade / fail-open-ledger behaviour — lives in the service, shared
/// with always-on chat grounding (gh#995), so there is one pipeline rather than two copies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only by construction (ADR-0025).</b> It injects only the read-only <see cref="INewsRetrievalService"/>
/// (itself wired from read / compute seams only), reaching <b>no</b> order / execution / gate / write type, so the
/// model can search and read the news but can never place, size, or modify an order (enforcement lives below the
/// model). The retrieved news text is <b>untrusted display data</b> the model reads — never instruction — exactly the
/// ADR-0025 boundary.
/// </para>
/// <para>
/// <b>Fail-closed on an unexpected throw.</b> The pipeline degrades every anticipated fault (no provider, a query that
/// will not embed, an unavailable / faulting read, a rerank degrade) to an <b>empty</b> result, which the tool
/// serialises as <c>{"results":[]}</c>. A malformed / query-less input is a compact error string. Only a genuine
/// caller cancellation propagates; any other unexpected fault (a seam violating its never-throw contract) is caught
/// here and failed <b>closed</b> to a compact error string the model reads (the <see cref="IChatTool"/> contract).
/// </para>
/// </remarks>
public sealed class SearchNewsTool : IChatTool
{
    private const int DefaultLimit = 5;
    private const int MaxLimit = 20;

    private readonly INewsRetrievalService _retrieval;
    private readonly ILogger<SearchNewsTool> _logger;

    /// <summary>Creates the tool over the shared read-only news retrieval pipeline.</summary>
    /// <param name="retrieval">The shared retrieval pipeline (embed → recall → hydrate → rerank), read-only by construction.</param>
    /// <param name="logger">The logger (an unexpected fault is logged, then failed closed to an error string).</param>
    public SearchNewsTool(INewsRetrievalService retrieval, ILogger<SearchNewsTool> logger)
    {
        ArgumentNullException.ThrowIfNull(retrieval);
        ArgumentNullException.ThrowIfNull(logger);
        _retrieval = retrieval;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "search_news";

    /// <inheritdoc />
    public LlmToolDefinition Definition => new(
        Name,
        "Semantically search the trader's ingested market news and soft-signal feed for items relevant to a free-text "
        + "query, most relevant first — each result is a headline, its source feed(s), when it published, and a short "
        + "snippet. Use when the trader asks what the news is saying about a theme, an instrument, or an event. "
        + "Read-only: it searches and reads news, and never places, sizes, or changes an order.",
        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\","
        + "\"description\":\"What to search the news for, in natural language.\"},\"limit\":{\"type\":\"integer\","
        + "\"description\":\"How many news items to return (default 5, max 20).\"}},\"required\":[\"query\"]}");

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(string inputJson, CancellationToken cancellationToken)
    {
        string? query;
        int limit;
        try
        {
            (query, limit) = ParseInput(inputJson);
        }
        catch (JsonException)
        {
            return Error("The tool input was not valid JSON.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return Error("A 'query' to search the news for is required.");
        }

        try
        {
            IReadOnlyList<RetrievedNewsItem> items = await _retrieval.RetrieveAsync(query, limit, cancellationToken);

            // Serialise the pipeline's items into the compact model-facing shape (unchanged from gh#987): headline,
            // the source feeds joined, the publish time, and the snippet. An empty result is {"results":[]}.
            var results = items
                .Select(item => new
                {
                    headline = item.Headline,
                    source = string.Join(", ", item.SourceFeeds),
                    publishedAt = item.PublishedAt,
                    snippet = item.Snippet,
                })
                .ToList();

            return JsonSerializer.Serialize(new { results });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine caller cancellation, not a fault to swallow
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "search_news faulted; returning a fail-closed tool error.");
            return Error("News could not be searched right now.");
        }
    }

    private static (string? Query, int Limit) ParseInput(string inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return (null, DefaultLimit);
        }

        using JsonDocument document = JsonDocument.Parse(inputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (null, DefaultLimit);
        }

        string? query = document.RootElement.TryGetProperty("query", out JsonElement queryElement)
            && queryElement.ValueKind == JsonValueKind.String
                ? queryElement.GetString()
                : null;

        int limit = document.RootElement.TryGetProperty("limit", out JsonElement limitElement)
            && limitElement.ValueKind == JsonValueKind.Number
            && limitElement.TryGetInt32(out int parsed)
                ? Math.Clamp(parsed, 1, MaxLimit)
                : DefaultLimit;

        return (query, limit);
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
}
