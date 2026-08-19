using System.Diagnostics;
using System.Text.Json;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Chat.Tools;

/// <summary>
/// The <c>search_news</c> chat tool (gh#987, R-6) — a read-only <b>semantic</b> search over the operator's ingested
/// news / soft-signal feed, and the <b>first <see cref="IReranker"/> consumer</b> (gh#975): it embeds the query
/// (<c>search_query</c>), takes a first-stage nearest-news recall (<see cref="INewsEmbeddingSimilarity"/>, gh#852),
/// hydrates the news text, and reranks it to the top-k the model reads (the cross-encoder second pass that sharpens
/// the recall).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only by construction (ADR-0025).</b> It injects only read / compute seams — the embedding provider, the
/// nearest-news read, the reranker — the scoped <see cref="TradingCopilotDbContext"/> used read-only
/// (<c>AsNoTracking</c>, no <c>SaveChanges</c>), and the fail-open AI-spend ledger (spend bookkeeping, not an order
/// path). It reaches <b>no</b> order / execution / gate / write type, so the model can search and read the news but
/// can never place, size, or modify an order (enforcement lives below the model). The retrieved news text is
/// <b>untrusted display data</b> the model reads — never instruction — exactly the ADR-0025 boundary.
/// </para>
/// <para>
/// <b>News is the R-20 global exception.</b> A <see cref="NewsRecord"/> is deliberately not <c>IUserOwned</c> — raw
/// news is shared / deployment-global reference data (like a market bar), so the hydrate read carries <b>no</b> owner
/// filter, the same posture as <c>get_quote</c>. The per-operator spend it bills is still stamped to the operator.
/// </para>
/// <para>
/// <b>Degrade, never throw, never fabricate.</b> No embedding provider, a query that will not embed (null vector), an
/// unavailable / faulting pgvector read, or a recall whose news has since gone all collapse to a single empty result
/// ("no matching news") rather than an error or invented items. A rerank that cannot run degrades to the recall's own
/// (identity) order — the seam guarantees that and the tool simply reads the returned <b>list order</b>, authoritative
/// on both the reranked and the passthrough path. Only a genuine caller cancellation propagates; any other unexpected
/// fault fails <b>closed</b> to a compact error string (the <see cref="IChatTool"/> contract).
/// </para>
/// <para>
/// <b>Spend is ledgered fail-open (ADR-0008, mirroring <c>NewsEmbeddingService</c>).</b> The query-embed is recorded
/// under <see cref="AiUsageFeature.Embed"/> and the rerank call under <see cref="AiUsageFeature.Chat"/> (rerank has no
/// <c>AiUsageFeature</c> of its own; it rides <c>Chat</c> with a null tier — see ADR-0008), each stamped to the
/// operator (<see cref="ICurrentUser"/>) with the caller-supplied clock. A ledger fault is logged and swallowed at
/// this boundary so bookkeeping can never fault the tool; an unavailable provider is short-circuited <i>before</i> the
/// embed call, so a degraded deployment never pays for — or ledgers — a query it would only discard.
/// </para>
/// </remarks>
public sealed class SearchNewsTool : IChatTool
{
    private const int DefaultLimit = 5;
    private const int MaxLimit = 20;

    /// <summary>How many first-stage candidates to recall per requested result — the reranker sharpens a wider set than it returns.</summary>
    private const int RecallMultiplier = 4;

    /// <summary>An absolute ceiling on the first-stage recall, so a large limit cannot fan out an unbounded candidate set.</summary>
    private const int MaxRecall = 50;

    /// <summary>The snippet length the summary is trimmed to, keeping the tool result compact.</summary>
    private const int SnippetLength = 240;

    private const string EmptyResult = "{\"results\":[]}";

    private readonly TradingCopilotDbContext _database;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly INewsEmbeddingSimilarity _similarity;
    private readonly IReranker _reranker;
    private readonly IAiUsageLedger _ledger;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SearchNewsTool> _logger;

    /// <summary>Creates the tool over the read seams, the read-only database, and the fail-open spend ledger.</summary>
    /// <param name="database">The scoped database — the global news read carries no owner filter (R-20 exception).</param>
    /// <param name="embeddingProvider">The embedding provider — Cohere when configured, the keyless no-op default otherwise.</param>
    /// <param name="similarity">The nearest-news read seam — the real pgvector read in production, a fake in unit tests.</param>
    /// <param name="reranker">The rerank seam (gh#975) — Cohere's cross-encoder when configured, the keyless passthrough otherwise.</param>
    /// <param name="ledger">The AIUsage spend ledger (gh#431): required, fail-open, stamped to the operator here.</param>
    /// <param name="currentUser">The authenticated operator (R-20) each ledger row is stamped to.</param>
    /// <param name="timeProvider">The clock supplying each ledger row's occurred-at (the ledger never reads a clock).</param>
    /// <param name="logger">The logger (a read fault is logged, then failed closed; a ledger fault is logged, then swallowed).</param>
    public SearchNewsTool(
        TradingCopilotDbContext database,
        IEmbeddingProvider embeddingProvider,
        INewsEmbeddingSimilarity similarity,
        IReranker reranker,
        IAiUsageLedger ledger,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        ILogger<SearchNewsTool> logger)
    {
        _database = database;
        _embeddingProvider = embeddingProvider;
        _similarity = similarity;
        _reranker = reranker;
        _ledger = ledger;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
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
            return await SearchAsync(query, limit, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine caller cancellation, not a read fault to swallow
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "search_news faulted; returning a fail-closed tool error.");
            return Error("News could not be searched right now.");
        }
    }

    private async Task<string> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (!_embeddingProvider.IsAvailable)
        {
            // No provider: semantic search is simply off (gh#109). No embed (no paid call) and no ledger row -- the
            // model reads an empty result and falls back to a plain answer, exactly as NewsSemanticSearch does.
            return EmptyResult;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        long embedStart = Stopwatch.GetTimestamp();
        EmbeddingResult embedding = await _embeddingProvider.EmbedQueryAsync(query, cancellationToken);
        await LedgerEmbedAsync(embedding, Stopwatch.GetElapsedTime(embedStart), now, cancellationToken);

        if (embedding.Vector is null)
        {
            // The query could not be embedded (rate limit, outage, unavailable mid-flight) -- the attempt is ledgered
            // above, but searching with a null vector would rank every row identically, so stop with an empty result.
            return EmptyResult;
        }

        int recall = Math.Min(limit * RecallMultiplier, MaxRecall);
        IReadOnlyList<SemanticNeighbor> neighbours =
            await _similarity.NearestNewsAsync(embedding.Vector, recall, cancellationToken);
        if (neighbours.Count == 0)
        {
            // No neighbours -- either genuinely nothing near, or an unavailable / faulting pgvector read the seam
            // already degraded to empty by contract. Either way: no matching news, and nothing to rerank.
            return EmptyResult;
        }

        IReadOnlyList<NewsRecord> hydrated = await HydrateAsync(neighbours, cancellationToken);
        if (hydrated.Count == 0)
        {
            return EmptyResult; // the recalled embeddings have no surviving news rows to show
        }

        IReadOnlyList<string> documents = [.. hydrated.Select(DocumentFor)];

        long rerankStart = Stopwatch.GetTimestamp();
        RerankResult rerank = await _reranker.RerankAsync(query, documents, limit, cancellationToken);
        await LedgerRerankAsync(rerank, Stopwatch.GetElapsedTime(rerankStart), now, cancellationToken);

        // Read the returned LIST ORDER -- authoritative on both the reranked and the passthrough (identity) path, so
        // the tool never re-sorts by a relevance score a degrade did not produce. The index bounds guard mirrors the
        // provider's own defensive drop of an out-of-range index.
        var results = rerank.Ranking
            .Where(ranked => ranked.Index >= 0 && ranked.Index < hydrated.Count)
            .Select(ranked => hydrated[ranked.Index])
            .Select(news => new
            {
                headline = news.Title,
                source = string.Join(", ", news.SourceFeeds),
                publishedAt = news.PublishedAt,
                snippet = Snippet(news.Summary),
            })
            .ToList();

        return JsonSerializer.Serialize(new { results });
    }

    /// <summary>
    /// Reads back the <see cref="NewsRecord"/> behind each recalled embedding by <see cref="NewsRecord.DedupKey"/>,
    /// preserving the nearest-first recall order the reranker will sharpen. <b>No owner filter</b>: news is the R-20
    /// global exception, so this reads deployment-global news (like the <c>get_quote</c> bar read). A recalled key with
    /// no surviving news row (an embedding can outlive its source) is dropped rather than fabricated.
    /// </summary>
    private async Task<IReadOnlyList<NewsRecord>> HydrateAsync(
        IReadOnlyList<SemanticNeighbor> neighbours, CancellationToken cancellationToken)
    {
        List<string> keys = [.. neighbours.Select(neighbour => neighbour.OwnerId)];

        // AsNoTracking: a read-only tool never tracks on the write-capable request context. Keyed by DedupKey so the
        // recall order can be re-applied in memory (a SQL IN(...) does not preserve the caller's order).
        Dictionary<string, NewsRecord> byKey = await _database.News
            .AsNoTracking()
            .Where(news => keys.Contains(news.DedupKey))
            .ToDictionaryAsync(news => news.DedupKey, cancellationToken);

        return [.. neighbours
            .Where(neighbour => byKey.ContainsKey(neighbour.OwnerId))
            .Select(neighbour => byKey[neighbour.OwnerId])];
    }

    /// <summary>The text handed to the reranker for a news item — headline + summary, the shape the embed pass also uses.</summary>
    private static string DocumentFor(NewsRecord news) => $"{news.Title}\n\n{news.Summary}";

    /// <summary>A compact snippet of the summary — trimmed to <see cref="SnippetLength"/> so the tool result stays small.</summary>
    private static string Snippet(string summary) =>
        summary.Length <= SnippetLength ? summary : string.Concat(summary.AsSpan(0, SnippetLength), "…");

    /// <summary>
    /// Ledgers the query-embed under <see cref="AiUsageFeature.Embed"/>, stamped to the operator (gh#987) — the
    /// owner-scoped counterpart of <c>NewsEmbeddingService</c>'s <c>SystemOwner</c>-stamped global embed. The ADR-0008
    /// shape: <c>Tier = null</c> (embeddings have no tier), <c>OutputTokens = 0</c> (an embed returns a vector), with
    /// the provider-priced tokens / cost riding the result.
    /// </summary>
    private async Task LedgerEmbedAsync(
        EmbeddingResult result, TimeSpan latency, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AiCallCost cost = new(
            AiUsageFeature.Embed, _embeddingProvider.Model, null, ToUsageOutcome(result.Outcome),
            result.BilledTokens, 0, result.EstimatedCostUsd, latency);

        await RecordAsync(cost, now, cancellationToken);
    }

    /// <summary>
    /// Ledgers the rerank call under <see cref="AiUsageFeature.Chat"/>, stamped to the operator (gh#987, ADR-0008):
    /// rerank has no <c>AiUsageFeature</c> of its own (no new value in this increment), so it rides <c>Chat</c> with
    /// <c>Tier = null</c> (no model tier). Rerank bills per <b>search</b>, so the billed search-unit count rides the
    /// input-quantity column (mirroring the embed row's billed-token placement) and the governor-relevant figure is
    /// <see cref="RerankResult.EstimatedCostUsd"/>; a degrade is a real zero-cost row, not an absence.
    /// </summary>
    private async Task LedgerRerankAsync(
        RerankResult result, TimeSpan latency, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AiCallCost cost = new(
            AiUsageFeature.Chat, _reranker.Model, null, ToUsageOutcome(result.Outcome),
            result.BilledSearches, 0, result.EstimatedCostUsd, latency);

        await RecordAsync(cost, now, cancellationToken);
    }

    /// <summary>
    /// Records one AI call, stamped to the operator (R-20) with the caller-supplied clock and the active trace. Guarded
    /// fail-open at this boundary (mirroring <c>NewsEmbeddingService.LedgerAsync</c>): <see cref="IAiUsageLedger"/> is
    /// already fail-open, but the seam admits any implementation, and a bookkeeping fault must never fault the tool.
    /// Only the caller's own cancellation escapes.
    /// </summary>
    private async Task RecordAsync(AiCallCost cost, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await _ledger.RecordAsync(
                new AiUsageEntry(_currentUser.UserId, cost, Activity.Current?.TraceId.ToString(), now),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error, "AIUsage ledger record failed for search_news ({Feature}); the tool result is unaffected.",
                cost.Feature);
        }
    }

    /// <summary>The ADR-0008 embed spend-outcome mapping: Embedded → Succeeded, RateLimited → RateLimited, else Failed.</summary>
    private static AiUsageOutcome ToUsageOutcome(EmbeddingOutcome outcome) => outcome switch
    {
        EmbeddingOutcome.Embedded => AiUsageOutcome.Succeeded,
        EmbeddingOutcome.RateLimited => AiUsageOutcome.RateLimited,
        _ => AiUsageOutcome.Failed,
    };

    /// <summary>The rerank spend-outcome mapping, mirroring the embed one: Reranked → Succeeded, RateLimited → RateLimited, else Failed.</summary>
    private static AiUsageOutcome ToUsageOutcome(RerankOutcome outcome) => outcome switch
    {
        RerankOutcome.Reranked => AiUsageOutcome.Succeeded,
        RerankOutcome.RateLimited => AiUsageOutcome.RateLimited,
        _ => AiUsageOutcome.Failed,
    };

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
