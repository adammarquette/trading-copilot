using System.Diagnostics;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// One retrieved piece of context (gh#1065, generalising gh#995's news-only item) — the compact, provider-neutral shape
/// every retrieval consumer reads, whatever kind it came from: what it is, a one-line title, who or what it came from,
/// when it happened, and a trimmed snippet.
/// </summary>
/// <remarks>
/// Carries <b>no</b> owner id and no relevance score. No owner because the pipeline has already scoped the read to the
/// operator (R-20) — passing an owner on would invite a consumer to re-check what is already enforced, or to render
/// somebody's id. No score because a consumer honours the returned <b>list order</b>, which is authoritative on both
/// the reranked and the passthrough path.
/// </remarks>
/// <param name="Kind">What kind of context this is, so a consumer can label it.</param>
/// <param name="Title">A one-line, <b>system-authored</b> summary — a news headline, or a rendered trade line.</param>
/// <param name="Attribution">Where it came from: a news item's source feeds, or a row's mode / state labels.</param>
/// <param name="OccurredAt">When it happened (UTC) — published, issued, or closed.</param>
/// <param name="Snippet">A compact snippet of the body, trimmed to keep a result small. <b>Untrusted display data.</b></param>
public sealed record RetrievedContextItem(
    RetrievalKind Kind,
    string Title,
    IReadOnlyList<string> Attribution,
    DateTimeOffset OccurredAt,
    string Snippet);

/// <summary>
/// The shared read-only <b>cross-kind retrieval pipeline</b> (gh#1065, R-6) — embed the query once, recall the nearest
/// stored embeddings of each asked kind, hydrate them, merge them nearest-first, and rerank to the top-k a consumer
/// reads. The generalisation of gh#995's news-only pipeline: a consumer now asks for "relevant <i>context</i>" and
/// says which kinds it wants, rather than asking for "relevant news".
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only by construction (ADR-0025 / ADR-0027).</b> It injects only read / compute seams — the embedding
/// provider, the ranked recall, the reranker — the scoped <see cref="TradingCopilotDbContext"/> used read-only
/// (<c>AsNoTracking</c>, no <c>SaveChanges</c>), and the fail-open AI-spend ledger. It reaches <b>no</b> order /
/// execution / gate / write type. What it returns is <b>untrusted display data</b> a consumer surfaces or grounds a
/// model on — never instruction (enforcement lives below the model).
/// </para>
/// <para>
/// <b>R-20 is enforced at the hydrate, and that is the whole point.</b> The <c>Embeddings</c> store is deliberately
/// <b>not</b> <c>IUserOwned</c> (it follows its owners, data dictionary §10), so the vector recall is
/// deployment-global and can legitimately return another operator's suggestion or journal entry. Every hydrate
/// therefore reads its owner's own table through the scoped, tenant-filtered context: a foreign row simply is not
/// there, so it is dropped exactly as a deleted owner is. News is the documented R-20 <b>global exception</b> — a
/// <see cref="NewsRecord"/> is shared reference data — so that one read carries no owner filter, the same posture as
/// <c>get_quote</c>. The per-operator spend the pipeline bills is stamped to the operator either way.
/// </para>
/// <para>
/// <b>Cross-kind ranking is real ranking.</b> Every kind's recall reports the same cosine-distance metric, so merging
/// the kinds and sorting by distance is meaningful rather than an arbitrary interleave. That merge order is also the
/// pipeline's <b>degrade</b> order: with no Cohere key the reranker returns the identity order, so what a keyless
/// deployment sees is a genuine nearest-first cross-kind list, not "all news, then all suggestions".
/// </para>
/// <para>
/// <b>The recall fan-out is per kind; the rerank payload is not.</b> Each kind recalls <c>min(k×4, 50)</c> candidates,
/// but the merged set is capped at the same ceiling before reranking — so adding a kind widens what can be found
/// without multiplying what the reranker is asked to score.
/// </para>
/// <para>
/// <b>Degrade to empty, never fabricate.</b> No embedding provider, a query that will not embed (null vector), an
/// unavailable / faulting pgvector read (empty by the seam's contract), or a recall whose owners have since gone all
/// collapse to an <b>empty result</b> rather than an invented one. A rerank that cannot run degrades to the merge's own
/// order — the seam guarantees that and the pipeline simply reads the returned <b>list order</b>. It does <b>not</b>
/// catch a contract-violating throw from a seam (the seams are contracted never to throw): that propagates so an
/// unexpected fault fails <b>closed at the caller</b> (the tool's error string, the endpoint's fail-open grounding),
/// never hidden here as an empty result. Only a genuine caller cancellation propagates as-is.
/// </para>
/// <para>
/// <b>Spend is ledgered fail-open (ADR-0008).</b> The query-embed is recorded under <see cref="AiUsageFeature.Embed"/>
/// and the rerank call under <see cref="AiUsageFeature.Chat"/> (rerank rides <c>Chat</c> with a null tier — ADR-0008),
/// each stamped to the operator (<see cref="ICurrentUser"/>) with the injected clock. The query is embedded
/// <b>once</b> for every kind — the vector is kind-independent, so embedding per kind would multiply the operator's
/// bill for the same vector. A ledger fault is logged and swallowed at this boundary so bookkeeping can never fault
/// retrieval; an unavailable provider is short-circuited <i>before</i> the embed call, so a degraded deployment never
/// pays for — or ledgers — a query it would only discard.
/// </para>
/// </remarks>
public interface IContextRetrievalService
{
    /// <summary>
    /// Retrieves the top-<paramref name="k"/> stored items most relevant to <paramref name="query"/> across the asked
    /// <paramref name="kinds"/>, most relevant first — the returned <b>list order</b> is authoritative (reranked, or
    /// the merge's nearest-first order on a rerank degrade).
    /// </summary>
    /// <param name="query">The natural-language query to embed and search for.</param>
    /// <param name="k">How many items to return; also the reranker's top-n. Each kind recalls <c>min(k×4, 50)</c>.</param>
    /// <param name="kinds">Which kinds of context to search — <see cref="RetrievalKinds.All"/> for everything.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The ranked items, or an <b>empty</b> list when retrieval is unavailable, finds nothing, or degrades.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A kind that cannot be retrieved was asked for.</exception>
    Task<IReadOnlyList<RetrievedContextItem>> RetrieveAsync(
        string query, int k, IReadOnlyCollection<RetrievalKind> kinds, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class ContextRetrievalService : IContextRetrievalService
{
    /// <summary>How many first-stage candidates to recall per requested result — the reranker sharpens a wider set than it returns.</summary>
    private const int RecallMultiplier = 4;

    /// <summary>An absolute ceiling on recall, applied per kind AND to the merged candidate set handed to the reranker.</summary>
    private const int MaxRecall = 50;

    /// <summary>The snippet length a body is trimmed to, keeping a retrieved item compact.</summary>
    private const int SnippetLength = 240;

    private readonly TradingCopilotDbContext _database;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IEmbeddingRecall _recall;
    private readonly IReranker _reranker;
    private readonly IAiUsageLedger _ledger;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ContextRetrievalService> _logger;

    /// <summary>Creates the pipeline over the read seams, the read-only database, and the fail-open spend ledger.</summary>
    /// <param name="database">The scoped, tenant-filtered database — the owner-scoped hydrates rely on that filter (R-20).</param>
    /// <param name="embeddingProvider">The embedding provider — Cohere when configured, the keyless no-op default otherwise.</param>
    /// <param name="recall">The ranked nearest-embedding read seam — the real pgvector read in production, a fake in unit tests.</param>
    /// <param name="reranker">The rerank seam (gh#975) — Cohere's cross-encoder when configured, the keyless passthrough otherwise.</param>
    /// <param name="ledger">The AIUsage spend ledger (gh#431): required, fail-open, stamped to the operator here.</param>
    /// <param name="currentUser">The authenticated operator (R-20) each ledger row is stamped to.</param>
    /// <param name="timeProvider">The clock supplying each ledger row's occurred-at (the ledger never reads a clock).</param>
    /// <param name="logger">The logger (a ledger fault is logged, then swallowed).</param>
    public ContextRetrievalService(
        TradingCopilotDbContext database,
        IEmbeddingProvider embeddingProvider,
        IEmbeddingRecall recall,
        IReranker reranker,
        IAiUsageLedger ledger,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        ILogger<ContextRetrievalService> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(recall);
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _embeddingProvider = embeddingProvider;
        _recall = recall;
        _reranker = reranker;
        _ledger = ledger;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedContextItem>> RetrieveAsync(
        string query, int k, IReadOnlyCollection<RetrievalKind> kinds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        if (kinds.Count == 0)
        {
            return []; // nothing asked for -- and no paid embed for a query nothing would search
        }

        // Validated BEFORE the paid embed: asking for a kind that cannot be retrieved is a caller error, and a caller
        // error must not cost the operator an embed call before it surfaces.
        foreach (RetrievalKind kind in kinds)
        {
            if (!RetrievalKinds.All.Contains(kind))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(kinds), kind, "That retrieval kind cannot be retrieved.");
            }
        }

        if (!_embeddingProvider.IsAvailable)
        {
            // No provider: semantic retrieval is simply off (gh#109). No embed (no paid call) and no ledger row -- an
            // empty result the consumer degrades on, the shared degrade posture for the embedding read seams.
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

        int recallPerKind = Math.Min(k * RecallMultiplier, MaxRecall);
        List<Candidate> candidates = [];

        // Distinct so a caller passing the same kind twice cannot double-recall it (and double the rerank payload).
        foreach (RetrievalKind kind in kinds.Distinct())
        {
            IReadOnlyList<SemanticNeighbor> neighbours =
                await _recall.NearestAsync(kind, embedding.Vector, recallPerKind, cancellationToken);
            if (neighbours.Count == 0)
            {
                continue; // genuinely nothing near, or a read the seam already degraded to empty by contract
            }

            candidates.AddRange(await HydrateAsync(kind, neighbours, cancellationToken));
        }

        if (candidates.Count == 0)
        {
            return []; // nothing recalled, or everything recalled belongs to someone else / no longer exists
        }

        // Merge nearest-first ACROSS kinds -- every kind reports the same cosine metric, so this is a real ranking and
        // not an interleave. OrderBy is stable, so equal distances keep their recall order. The merged set is capped at
        // the same ceiling one kind gets, so the rerank payload does not grow with the number of kinds asked for.
        List<Candidate> merged = [.. candidates.OrderBy(candidate => candidate.Distance).Take(MaxRecall)];
        IReadOnlyList<string> documents = [.. merged.Select(candidate => candidate.Document)];

        long rerankStart = Stopwatch.GetTimestamp();
        RerankResult rerank = await _reranker.RerankAsync(query, documents, k, cancellationToken);
        await LedgerRerankAsync(rerank, Stopwatch.GetElapsedTime(rerankStart), now, cancellationToken);

        // Read the returned LIST ORDER -- authoritative on both the reranked and the passthrough (identity) path, so
        // the pipeline never re-sorts by a relevance score a degrade did not produce. The index bounds guard mirrors
        // the provider's own defensive drop of an out-of-range index.
        return [.. rerank.Ranking
            .Where(ranked => ranked.Index >= 0 && ranked.Index < merged.Count)
            .Select(ranked => merged[ranked.Index].Item)];
    }

    /// <summary>
    /// One recalled row, carried from hydrate to rerank: its distance (the cross-kind merge key), the document text the
    /// reranker cross-encodes, and the projected item a consumer finally reads.
    /// </summary>
    private sealed record Candidate(double Distance, string Document, RetrievedContextItem Item);

    /// <summary>
    /// Reads back the rows behind one kind's recalled embeddings, preserving the nearest-first recall order the merge
    /// and the reranker will sharpen. <b>This is the R-20 enforcement point</b> for the owner-scoped kinds: the read
    /// goes through the scoped, tenant-filtered context, so another operator's row is simply absent — dropped exactly
    /// as a recalled owner that no longer exists is, and never fabricated.
    /// </summary>
    private Task<IReadOnlyList<Candidate>> HydrateAsync(
        RetrievalKind kind, IReadOnlyList<SemanticNeighbor> neighbours, CancellationToken cancellationToken) => kind switch
        {
            RetrievalKind.News => HydrateNewsAsync(neighbours, cancellationToken),
            RetrievalKind.Suggestion => HydrateSuggestionsAsync(neighbours, cancellationToken),
            RetrievalKind.JournalEntry => HydrateJournalEntriesAsync(neighbours, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "That retrieval kind has no hydrate."),
        };

    /// <summary>
    /// Hydrates recalled news by <see cref="NewsRecord.DedupKey"/>. <b>No owner filter</b>: news is the R-20 global
    /// exception, so this reads deployment-global news (like the <c>get_quote</c> bar read).
    /// </summary>
    private async Task<IReadOnlyList<Candidate>> HydrateNewsAsync(
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
            .Select(neighbour => ToCandidate(neighbour.Distance, byKey[neighbour.OwnerId]))];
    }

    /// <summary>
    /// Hydrates recalled suggestions by id, <b>through the tenant query filter</b> (R-20) — a suggestion the recall
    /// found but this operator does not own is absent from the read and therefore dropped.
    /// </summary>
    private async Task<IReadOnlyList<Candidate>> HydrateSuggestionsAsync(
        IReadOnlyList<SemanticNeighbor> neighbours, CancellationToken cancellationToken)
    {
        IReadOnlyList<(Guid Id, double Distance)> recalled = ParseGuidOwners(neighbours);
        if (recalled.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. recalled.Select(entry => entry.Id)];
        Dictionary<Guid, Suggestion> byId = await _database.Suggestions
            .AsNoTracking()
            .Where(suggestion => ids.Contains(suggestion.Id))
            .ToDictionaryAsync(suggestion => suggestion.Id, cancellationToken);

        return [.. recalled
            .Where(entry => byId.ContainsKey(entry.Id))
            .Select(entry => ToCandidate(entry.Distance, byId[entry.Id]))];
    }

    /// <summary>
    /// Hydrates recalled journal entries (closed trades) by id, <b>through the tenant query filter</b> (R-20) — the
    /// same enforcement as the suggestion hydrate above.
    /// </summary>
    private async Task<IReadOnlyList<Candidate>> HydrateJournalEntriesAsync(
        IReadOnlyList<SemanticNeighbor> neighbours, CancellationToken cancellationToken)
    {
        IReadOnlyList<(Guid Id, double Distance)> recalled = ParseGuidOwners(neighbours);
        if (recalled.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. recalled.Select(entry => entry.Id)];
        Dictionary<Guid, Trade> byId = await _database.Trades
            .AsNoTracking()
            .Where(trade => ids.Contains(trade.Id))
            .ToDictionaryAsync(trade => trade.Id, cancellationToken);

        return [.. recalled
            .Where(entry => byId.ContainsKey(entry.Id))
            .Select(entry => ToCandidate(entry.Distance, byId[entry.Id]))];
    }

    /// <summary>
    /// Parses the Guid-keyed kinds' recalled owner ids, dropping any that will not parse. <c>OwnerId</c> is text in the
    /// store so any key shape fits (a news item is keyed by its dedup string), which means a Guid-keyed kind must
    /// tolerate a malformed row by ignoring it rather than throwing mid-retrieval.
    /// </summary>
    private static IReadOnlyList<(Guid Id, double Distance)> ParseGuidOwners(IReadOnlyList<SemanticNeighbor> neighbours) =>
        [.. neighbours
            .Select(neighbour => (Parsed: Guid.TryParse(neighbour.OwnerId, out Guid id), Id: id, neighbour.Distance))
            .Where(entry => entry.Parsed)
            .Select(entry => (entry.Id, entry.Distance))];

    private static Candidate ToCandidate(double distance, NewsRecord news) => new(
        distance,
        ContextEmbeddingContent.ForNews(news),
        new RetrievedContextItem(
            RetrievalKind.News, news.Title, [.. news.SourceFeeds], news.PublishedAt, Snippet(news.Summary)));

    private static Candidate ToCandidate(double distance, Suggestion suggestion) => new(
        distance,
        ContextEmbeddingContent.ForSuggestion(suggestion),
        new RetrievedContextItem(
            RetrievalKind.Suggestion,
            ContextEmbeddingContent.SuggestionLine(suggestion),
            [suggestion.Mode.ToString(), suggestion.State.ToString()],
            suggestion.CreatedAt,
            Snippet(suggestion.Rationale)));

    private static Candidate ToCandidate(double distance, Trade trade) => new(
        distance,
        ContextEmbeddingContent.ForJournalEntry(trade),
        new RetrievedContextItem(
            RetrievalKind.JournalEntry,
            ContextEmbeddingContent.JournalEntryLine(trade),
            [trade.Mode.ToString()],
            // Only closed trades are embedded, so ClosedAt is present in practice; the fallback keeps a defective row
            // renderable rather than throwing inside a retrieval that is off the trading path.
            trade.ClosedAt ?? default,
            Snippet(ContextEmbeddingContent.JournalEntryDetail(trade))));

    /// <summary>A compact snippet of a body — trimmed to <see cref="SnippetLength"/> so a retrieved item stays small.</summary>
    private static string Snippet(string body) =>
        body.Length <= SnippetLength ? body : string.Concat(body.AsSpan(0, SnippetLength), "…");

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
                error, "AIUsage ledger record failed for context retrieval ({Feature}); the result is unaffected.",
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
