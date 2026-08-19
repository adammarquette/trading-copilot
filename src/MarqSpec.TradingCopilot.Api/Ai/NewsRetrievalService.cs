using System.Diagnostics;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// One retrieved news item (gh#995) — the compact, provider-neutral shape a retrieval consumer reads: the headline,
/// its source feed(s), when it published, and a trimmed snippet. Carries <b>no</b> owner (news is the R-20 global
/// exception) and no relevance score (a consumer honours the returned <b>list order</b>, authoritative on both the
/// reranked and the passthrough path).
/// </summary>
/// <param name="Headline">The story's headline.</param>
/// <param name="SourceFeeds">The feed(s) that carried it (for example <c>finnhub</c>, <c>tiingo</c>).</param>
/// <param name="PublishedAt">When the story published (UTC).</param>
/// <param name="Snippet">A compact snippet of the summary, trimmed to keep a result small.</param>
public sealed record RetrievedNewsItem(
    string Headline, IReadOnlyList<string> SourceFeeds, DateTimeOffset PublishedAt, string Snippet);

/// <summary>
/// The shared read-only <b>news retrieval pipeline</b> (gh#995, R-6 / R-2) — embed the query, recall the nearest
/// stored news, hydrate it, and rerank it to the top-k a consumer reads. Extracted from the <c>search_news</c> chat
/// tool (gh#987) so both a model-driven tool call <b>and</b> always-on chat grounding (gh#995) go through one
/// pipeline rather than two copies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only by construction (ADR-0025 / ADR-0027).</b> It injects only read / compute seams — the embedding
/// provider, the nearest-news read, the reranker — the scoped <see cref="TradingCopilotDbContext"/> used read-only
/// (<c>AsNoTracking</c>, no <c>SaveChanges</c>), and the fail-open AI-spend ledger. It reaches <b>no</b> order /
/// execution / gate / write type. What it returns is <b>untrusted display data</b> a consumer surfaces or grounds a
/// model on — never instruction (enforcement lives below the model).
/// </para>
/// <para>
/// <b>News is the R-20 global exception.</b> A <see cref="NewsRecord"/> is deliberately not <c>IUserOwned</c> — raw
/// news is shared / deployment-global reference data — so the hydrate read carries <b>no</b> owner filter, the same
/// posture as <c>get_quote</c>. The per-operator spend it bills is still stamped to the operator.
/// </para>
/// <para>
/// <b>Degrade to empty, never fabricate.</b> No embedding provider, a query that will not embed (null vector), an
/// unavailable / faulting pgvector read (empty by the seam's contract), or a recall whose news has since gone all
/// collapse to an <b>empty result</b> rather than an invented one. A rerank that cannot run degrades to the recall's
/// own (identity) order — the seam guarantees that and the pipeline simply reads the returned <b>list order</b>. It
/// does <b>not</b> catch a contract-violating throw from a seam (the seams are contracted never to throw): that
/// propagates so an unexpected fault fails <b>closed at the caller</b> (the tool's error string, the endpoint's
/// fail-open grounding), never hidden here as an empty result. Only a genuine caller cancellation propagates as-is.
/// </para>
/// <para>
/// <b>Spend is ledgered fail-open (ADR-0008).</b> The query-embed is recorded under <see cref="AiUsageFeature.Embed"/>
/// and the rerank call under <see cref="AiUsageFeature.Chat"/> (rerank rides <c>Chat</c> with a null tier — ADR-0008),
/// each stamped to the operator (<see cref="ICurrentUser"/>) with the injected clock. A ledger fault is logged and
/// swallowed at this boundary so bookkeeping can never fault retrieval; an unavailable provider is short-circuited
/// <i>before</i> the embed call, so a degraded deployment never pays for — or ledgers — a query it would only discard.
/// </para>
/// </remarks>
public interface INewsRetrievalService
{
    /// <summary>
    /// Retrieves the top-<paramref name="k"/> stored news items most relevant to <paramref name="query"/>, most
    /// relevant first — the returned <b>list order</b> is authoritative (reranked, or the recall's identity order on a
    /// rerank degrade).
    /// </summary>
    /// <param name="query">The natural-language query to embed and search the news for.</param>
    /// <param name="k">How many items to return; also the reranker's top-n. Recall fans out to <c>min(k×4, 50)</c>.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The ranked items, or an <b>empty</b> list when retrieval is unavailable, finds nothing, or degrades.</returns>
    Task<IReadOnlyList<RetrievedNewsItem>> RetrieveAsync(string query, int k, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class NewsRetrievalService : INewsRetrievalService
{
    /// <summary>How many first-stage candidates to recall per requested result — the reranker sharpens a wider set than it returns.</summary>
    private const int RecallMultiplier = 4;

    /// <summary>An absolute ceiling on the first-stage recall, so a large k cannot fan out an unbounded candidate set.</summary>
    private const int MaxRecall = 50;

    /// <summary>The snippet length the summary is trimmed to, keeping a retrieved item compact.</summary>
    private const int SnippetLength = 240;

    private readonly TradingCopilotDbContext _database;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly INewsEmbeddingSimilarity _similarity;
    private readonly IReranker _reranker;
    private readonly IAiUsageLedger _ledger;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NewsRetrievalService> _logger;

    /// <summary>Creates the pipeline over the read seams, the read-only database, and the fail-open spend ledger.</summary>
    /// <param name="database">The scoped database — the global news read carries no owner filter (R-20 exception).</param>
    /// <param name="embeddingProvider">The embedding provider — Cohere when configured, the keyless no-op default otherwise.</param>
    /// <param name="similarity">The nearest-news read seam — the real pgvector read in production, a fake in unit tests.</param>
    /// <param name="reranker">The rerank seam (gh#975) — Cohere's cross-encoder when configured, the keyless passthrough otherwise.</param>
    /// <param name="ledger">The AIUsage spend ledger (gh#431): required, fail-open, stamped to the operator here.</param>
    /// <param name="currentUser">The authenticated operator (R-20) each ledger row is stamped to.</param>
    /// <param name="timeProvider">The clock supplying each ledger row's occurred-at (the ledger never reads a clock).</param>
    /// <param name="logger">The logger (a ledger fault is logged, then swallowed).</param>
    public NewsRetrievalService(
        TradingCopilotDbContext database,
        IEmbeddingProvider embeddingProvider,
        INewsEmbeddingSimilarity similarity,
        IReranker reranker,
        IAiUsageLedger ledger,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        ILogger<NewsRetrievalService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(similarity);
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
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
    public async Task<IReadOnlyList<RetrievedNewsItem>> RetrieveAsync(
        string query, int k, CancellationToken cancellationToken)
    {
        if (!_embeddingProvider.IsAvailable)
        {
            // No provider: semantic retrieval is simply off (gh#109). No embed (no paid call) and no ledger row -- an
            // empty result the consumer degrades on, the shared degrade posture for the news-embedding read seam.
            return [];
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        long embedStart = Stopwatch.GetTimestamp();
        EmbeddingResult embedding = await _embeddingProvider.EmbedQueryAsync(query, cancellationToken);
        await LedgerEmbedAsync(embedding, Stopwatch.GetElapsedTime(embedStart), now, cancellationToken);

        if (embedding.Vector is null)
        {
            // The query could not be embedded (rate limit, outage, unavailable mid-flight) -- the attempt is ledgered
            // above, but searching with a null vector would rank every row identically, so stop with an empty result.
            return [];
        }

        int recall = Math.Min(k * RecallMultiplier, MaxRecall);
        IReadOnlyList<SemanticNeighbor> neighbours =
            await _similarity.NearestNewsAsync(embedding.Vector, recall, cancellationToken);
        if (neighbours.Count == 0)
        {
            // No neighbours -- either genuinely nothing near, or an unavailable / faulting pgvector read the seam
            // already degraded to empty by contract. Either way: no matching news, and nothing to rerank.
            return [];
        }

        IReadOnlyList<NewsRecord> hydrated = await HydrateAsync(neighbours, cancellationToken);
        if (hydrated.Count == 0)
        {
            return []; // the recalled embeddings have no surviving news rows to show
        }

        IReadOnlyList<string> documents = [.. hydrated.Select(DocumentFor)];

        long rerankStart = Stopwatch.GetTimestamp();
        RerankResult rerank = await _reranker.RerankAsync(query, documents, k, cancellationToken);
        await LedgerRerankAsync(rerank, Stopwatch.GetElapsedTime(rerankStart), now, cancellationToken);

        // Read the returned LIST ORDER -- authoritative on both the reranked and the passthrough (identity) path, so
        // the pipeline never re-sorts by a relevance score a degrade did not produce. The index bounds guard mirrors
        // the provider's own defensive drop of an out-of-range index.
        return [.. rerank.Ranking
            .Where(ranked => ranked.Index >= 0 && ranked.Index < hydrated.Count)
            .Select(ranked => hydrated[ranked.Index])
            .Select(news => new RetrievedNewsItem(
                news.Title, [.. news.SourceFeeds], news.PublishedAt, Snippet(news.Summary)))];
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

        // AsNoTracking: a read-only pipeline never tracks on the write-capable request context. Keyed by DedupKey so
        // the recall order can be re-applied in memory (a SQL IN(...) does not preserve the caller's order).
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

    /// <summary>A compact snippet of the summary — trimmed to <see cref="SnippetLength"/> so a retrieved item stays small.</summary>
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
    /// rerank has no <c>AiUsageFeature</c> of its own, so it rides <c>Chat</c> with <c>Tier = null</c> (no model tier).
    /// Rerank bills per <b>search</b>, so the billed search-unit count rides the input-quantity column (mirroring the
    /// embed row's billed-token placement); a degrade is a real zero-cost row, not an absence.
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
    /// Records one AI call, stamped to the operator (R-20) with the injected clock and the active trace. Guarded
    /// fail-open at this boundary (mirroring <c>NewsEmbeddingService.LedgerAsync</c>): <see cref="IAiUsageLedger"/> is
    /// already fail-open, but the seam admits any implementation, and a bookkeeping fault must never fault retrieval.
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
                error, "AIUsage ledger record failed for news retrieval ({Feature}); the result is unaffected.",
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
}
