using System.Diagnostics;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Flatten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// Populates the <c>pgvector</c> embedding behind each ingested news item (gh#377, R-2): the deferred VEC half of
/// the <c>SoftSignal (NewsItem)</c> entity, the concrete instance of #103's open "which entities embed" question —
/// news is the R-2 reference implementation. Mirrors <see cref="Relevance.NewsRelevanceService"/>'s pass shape —
/// batch loop, per-page <c>SaveChanges</c> + <c>ChangeTracker.Clear</c> — over a different store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Operator-gated + graceful degrade (gh#109 / gh#403).</b> With no embedding provider configured the pass is a
/// pure no-op: the structured <see cref="NewsRecord"/> rows are untouched, nothing throws, and semantic match stays
/// degraded to non-semantic until an operator sets <c>Cohere__ApiKey</c>.
/// </para>
/// <para>
/// <b>Idempotent via <see cref="EmbeddingContentHash"/>.</b> A news item needs embedding when it has no
/// <see cref="EmbeddingRecord"/> for <c>(SoftSignal, DedupKey, provider.Model)</c>, or that record's stored hash no
/// longer matches its current content — computed from the exact text handed to the provider (title + summary), so
/// a harmless re-poll (new source feed, no content change) is recognised and never re-billed. An owner whose hash
/// already matches is <i>touched</i> (its <see cref="EmbeddingRecord.RecordedAt"/> is bumped without a re-embed) so
/// it drops out of the next pass's candidate set rather than being re-checked forever.
/// </para>
/// <para>
/// <b>The AI-spend cap (gh#448, ADR-0008) mirrors <see cref="Triggers.TriggerEvaluationService"/></b>: the shared
/// platform-wide <see cref="AiSpendBudget"/> (absent ⇒ inert) is read once per pass from the <c>AIUsage</c> ledger
/// floor, and re-evaluated before every embed call so a long pass still stops mid-page once the budget is reached
/// — logged once, never a throw. A spend-read fault fails <b>open</b> (the pass runs un-gated), the deliberate
/// inverse of the trade-safety gate, because this guards a soft-dollar budget, not capital-at-risk.
/// </para>
/// <para>
/// <b>AIUsage recording (gh#436) is the live producer</b> the sentinel <see cref="SystemOwner"/> was readied for:
/// embedding deployment-global news is infrastructure cost, not an operator's trading decision, so every call —
/// success or failure — ledgers one row stamped to <see cref="SystemOwner.Id"/>, <c>Feature = Embed</c>,
/// <c>Tier = null</c>, <c>OutputTokens = 0</c>, with <see cref="EmbeddingOutcome"/> mapped onto
/// <see cref="AiUsageOutcome"/> exactly as ADR-0008 pins it (Embedded → Succeeded, RateLimited → RateLimited,
/// Failed → Failed). The ledger write is guarded at this boundary too (mirroring the trigger scan), so a
/// contract-violating ledger implementation can never abort the pass.
/// </para>
/// <para>
/// <b>Relational-only, by design (gh#109).</b> <see cref="EmbeddingRecord"/>'s <c>Vector</c> column has no
/// in-memory-provider mapping, so the batch query and upsert below are exercisable only against real Postgres
/// (QA integration tier, #361/#362); the degrade, the cap gate, and the content-hash decision are the parts unit
/// tests can and do cover without a database round-trip of the vector itself.
/// </para>
/// </remarks>
public sealed class NewsEmbeddingService
{
    private const int BatchSize = 500;

    private readonly TradingCopilotDbContext _database;
    private readonly IEmbeddingProvider _provider;
    private readonly IAiUsageLedger _ledger;
    private readonly IAiSpendGovernor _governor;
    private readonly AiSpendBudget? _budget;
    private readonly ILogger<NewsEmbeddingService> _logger;

    /// <summary>Creates the service over the scoped database and the embedding + spend seams.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="provider">The embedding provider — Cohere when configured, the keyless no-op default otherwise.</param>
    /// <param name="ledger">The AIUsage spend ledger (gh#431): required, fail-open, stamped to <see cref="SystemOwner"/> here.</param>
    /// <param name="governor">The platform-level AI-spend gate (gh#448) consulted before each embed call.</param>
    /// <param name="governorOptions">The governor's budget config; absent (null budget) leaves the governor inert.</param>
    /// <param name="logger">The logger.</param>
    public NewsEmbeddingService(
        TradingCopilotDbContext database,
        IEmbeddingProvider provider,
        IAiUsageLedger ledger,
        IAiSpendGovernor governor,
        IOptions<GovernorOptions> governorOptions,
        ILogger<NewsEmbeddingService> logger)
    {
        ArgumentNullException.ThrowIfNull(governorOptions);

        _database = database;
        _provider = provider;
        _ledger = ledger;
        _governor = governor;
        _budget = governorOptions.Value.ToBudget(); // null == inert (no cap configured); computed once per pass
        _logger = logger;
    }

    /// <summary>Runs one embedding pass over the news that needs it.</summary>
    /// <param name="now">The instant to stamp recorded rows and ledger entries with.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many news items were newly embedded or re-embedded this pass.</returns>
    public async Task<int> EmbedPendingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_provider.IsAvailable)
        {
            // No key, no pgvector, deliberately disabled — an ordinary state (gh#109). The structured NewsRecord
            // rows are untouched; nothing throws. UnavailableEmbeddingProvider already announces this once at
            // ERROR on first use elsewhere, so this stays at DEBUG rather than doubling that signal.
            _logger.LogDebug("No embedding provider is available; the news-embedding pass is a no-op.");
            return 0;
        }

        GovernorPass? governorPass = await BuildGovernorPassAsync(now, cancellationToken);
        if (IsCapReached(governorPass))
        {
            _logger.LogInformation(
                "The daily AI-spend budget is reached; the news-embedding pass is paused until it resets.");
            return 0;
        }

        string model = _provider.Model;
        int total = 0;

        // Page the work exactly as NewsRelevanceService does: an unbounded backlog (first run, or a long outage)
        // must not be loaded into one SaveChanges, and a row that was actually handled this page (embedded, or
        // touched as unchanged) drops out of the next page's query, guaranteeing progress.
        while (true)
        {
            // "Needs embedding" as a NOT EXISTS: no EmbeddingRecord for (SoftSignal, DedupKey, model) at all, OR
            // the news row was re-written since one was last recorded (a new source feed, or a future content
            // revision) -- a conservative superset the content hash below resolves cheaply, without a paid call,
            // when nothing actually changed.
            List<NewsRecord> batch = await _database.News
                .Where(news => !_database.Embeddings.Any(embedding =>
                    embedding.OwnerKind == EmbeddingOwnerKind.SoftSignal
                    && embedding.OwnerId == news.DedupKey
                    && embedding.Model == model
                    && embedding.RecordedAt >= news.RecordedAt))
                .OrderBy(news => news.PublishedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            bool progressed = false;
            bool capReachedMidPage = false;

            foreach (NewsRecord news in batch)
            {
                // Re-evaluated per item (mirroring the trigger scan's rising-spend tally): a long pass can cross
                // the cap mid-page, and later items must see that rather than only the state at pass start.
                if (IsCapReached(governorPass))
                {
                    capReachedMidPage = true;
                    break;
                }

                string content = ContentFor(news);
                string hash = EmbeddingContentHash.For(content);

                EmbeddingRecord? existing = await _database.Embeddings.FirstOrDefaultAsync(
                    embedding => embedding.OwnerKind == EmbeddingOwnerKind.SoftSignal
                        && embedding.OwnerId == news.DedupKey
                        && embedding.Model == model,
                    cancellationToken);

                if (existing is not null && existing.ContentHash == hash)
                {
                    // Idempotent skip: the content behind this owner has not changed, so the paid call is skipped.
                    // Still touch RecordedAt so this row drops out of the candidate set above next pass -- otherwise
                    // a harmless re-poll (a new source feed, same title/summary) would be re-checked forever.
                    existing.RecordedAt = now;
                    progressed = true;
                    continue;
                }

                long startedTicks = Stopwatch.GetTimestamp();
                EmbeddingResult result = await _provider.EmbedAsync(content, cancellationToken);
                TimeSpan latency = Stopwatch.GetElapsedTime(startedTicks);

                await LedgerAsync(result, latency, now, cancellationToken);
                governorPass?.Accrue(result.EstimatedCostUsd);

                if (result.Vector is null)
                {
                    // Rate-limited, unavailable mid-pass, or a provider fault -- skip this item THIS pass. It
                    // retries next pass (still a candidate: no EmbeddingRecord, or a stale one); the structured
                    // NewsRecord row is never touched.
                    continue;
                }

                Upsert(existing, news.DedupKey, model, hash, result.Vector, now);
                progressed = true;
                total++;
            }

            await _database.SaveChangesAsync(cancellationToken);
            _database.ChangeTracker.Clear(); // release the page before loading the next

            if (capReachedMidPage)
            {
                _logger.LogInformation(
                    "The daily AI-spend budget is reached; the news-embedding pass stops for this run.");
                break;
            }

            // No item in this page could be handled (the provider is down/rate-limited for the whole pass) --
            // stop rather than re-querying the SAME unresolved page forever within this one run. It retries next
            // pass exactly as a per-item failure does.
            if (!progressed || batch.Count < BatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            _logger.LogDebug("Embedded {Count} news item(s).", total);
        }

        return total;
    }

    /// <summary>Adds a new <see cref="EmbeddingRecord"/>, or updates <paramref name="existing"/> in place.</summary>
    private void Upsert(
        EmbeddingRecord? existing, string dedupKey, string model, string contentHash, IReadOnlyList<float> vector, DateTimeOffset now)
    {
        // Vector's constructor takes a ReadOnlyMemory<float>; a float[] converts to that implicitly, and
        // IReadOnlyList<float> (what the provider seam returns) does not, so it is materialized explicitly.
        Vector embedding = new(vector.ToArray());

        if (existing is null)
        {
            _database.Embeddings.Add(new EmbeddingRecord
            {
                OwnerKind = EmbeddingOwnerKind.SoftSignal,
                OwnerId = dedupKey,
                Model = model,
                Dimensions = _provider.Dimensions,
                Embedding = embedding,
                ContentHash = contentHash,
                RecordedAt = now,
            });
            return;
        }

        existing.Dimensions = _provider.Dimensions;
        existing.Embedding = embedding;
        existing.ContentHash = contentHash;
        existing.RecordedAt = now;
    }

    /// <summary>
    /// Ledgers one embed call's spend (gh#436, gh#377): stamped to the deployment <see cref="SystemOwner"/> sentinel
    /// (embedding deployment-global news is infrastructure cost, never an operator's trading decision), regardless
    /// of outcome -- a rate-limited or failed call is metered too, mirroring how <see cref="IEmbeddingMetrics"/>
    /// meters every call including a failover.
    /// </summary>
    private async Task LedgerAsync(EmbeddingResult result, TimeSpan latency, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // All positional (mirrors LlmTriggerReviewer's own AiCallCost construction): Tier null (embeddings have no
        // model tier) and OutputTokens 0 (an embed returns a vector, not completion tokens) are the ADR-0008-pinned
        // shape; InputTokens/EstimatedCostUsd are the provider-priced facts riding the result.
        AiCallCost cost = new(
            AiUsageFeature.Embed, _provider.Model, null, ToUsageOutcome(result.Outcome),
            result.BilledTokens, 0, result.EstimatedCostUsd, latency);

        // Guarded at this boundary too (mirroring TriggerEvaluationService): IAiUsageLedger's contract is already
        // fail-open, but the seam admits any implementation, and a bookkeeping fault must never abort a pass that
        // is otherwise making progress. Only the caller's own cancellation escapes.
        try
        {
            await _ledger.RecordAsync(
                new AiUsageEntry(SystemOwner.Id, cost, Activity.Current?.TraceId.ToString(), now), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "AIUsage ledger record failed for the news-embedding pass; embedding continues regardless.");
        }
    }

    /// <summary>The pinned ADR-0008 spend-outcome mapping: Embedded → Succeeded, RateLimited → RateLimited, Failed → Failed.</summary>
    private static AiUsageOutcome ToUsageOutcome(EmbeddingOutcome outcome) => outcome switch
    {
        EmbeddingOutcome.Embedded => AiUsageOutcome.Succeeded,
        EmbeddingOutcome.RateLimited => AiUsageOutcome.RateLimited,
        _ => AiUsageOutcome.Failed,
    };

    /// <summary>The exact text handed to the provider — title + summary, the same string the content hash is over.</summary>
    private static string ContentFor(NewsRecord news) => $"{news.Title}\n\n{news.Summary}";

    /// <summary>Whether the budget is configured and already spent — a no-op check when the governor is inert.</summary>
    private bool IsCapReached(GovernorPass? governorPass) =>
        governorPass is not null && _governor.Evaluate(governorPass.Budget, governorPass.SpentUsd).IsBlocked;

    /// <summary>Builds the per-pass governor tally, or <see langword="null"/> when inert or the spend read faulted.</summary>
    private async Task<GovernorPass?> BuildGovernorPassAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_budget is null)
        {
            return null; // no cap configured -- the governor is inert, mirroring TriggerEvaluationService pre-gh#448
        }

        try
        {
            decimal spent = await ReadWindowSpendAsync(CentralDayStartUtc(now), cancellationToken);
            return new GovernorPass(_budget, spent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // FAIL-OPEN -- the deliberate inverse of a fail-closed risk gate (mirrors TriggerEvaluationService,
            // gh#448). This guards a soft-dollar BUDGET, not capital-at-risk: a spend-read blip must not pause
            // embedding, and must not abort the pass. Log loudly, then run this pass un-gated; the next pass re-reads.
            _logger.LogError(error, "AI-spend governor could not read spend this pass; embedding runs un-gated.");
            return null;
        }
    }

    /// <summary>
    /// Reads the platform-wide AI spend since <paramref name="windowStart"/> -- the SAME shared floor
    /// <see cref="Triggers.TriggerEvaluationService"/> reads (<c>IgnoreQueryFilters</c>, gh#448: one shared LLM +
    /// embeddings account funds every user, R-20), so a heavy embed backlog and agent-review triage draw against one
    /// deployment-wide daily cap. The <c>(decimal?)</c> cast + <c>?? 0m</c> is defensive, not load-bearing: an
    /// empty-window <c>SUM</c> is a SQL <c>NULL</c> that EF 10 + Npgsql coerce to <c>0</c> on this stack (gh#479).
    /// </summary>
    private async Task<decimal> ReadWindowSpendAsync(DateTimeOffset windowStart, CancellationToken cancellationToken) =>
        await _database.AiUsage
            .IgnoreQueryFilters()
            .Where(record => record.OccurredAt >= windowStart)
            .Select(record => (decimal?)record.EstimatedCostUsd)
            .SumAsync(cancellationToken) ?? 0m;

    // The daily spend window resets at CENTRAL-trading-day midnight, exactly as the governor and the daily risk
    // governor / auto-flatten do (a UTC date would split a live CME session). The boundary lives on MarketClock
    // (gh#741) -- the one definition they share; this stays a private alias so the call site reads the same.
    private static DateTimeOffset CentralDayStartUtc(DateTimeOffset now) => MarketClock.CentralDayStartUtc(now);

    /// <summary>
    /// The per-pass AI-spend tally — seeded from the once-per-pass ledger read, then accrued by each embed call so
    /// later items this pass evaluate against rising spend (mirrors <see cref="Triggers.TriggerEvaluationService"/>'s
    /// own nested tally). Single-threaded within a pass, so no locking.
    /// </summary>
    private sealed class GovernorPass(AiSpendBudget budget, decimal spentAtStart)
    {
        /// <summary>The operator's daily budget for the pass.</summary>
        public AiSpendBudget Budget { get; } = budget;

        /// <summary>Spend so far this window -- the ledger floor at pass start plus each call accrued since.</summary>
        public decimal SpentUsd { get; private set; } = spentAtStart;

        /// <summary>Accrues one call's estimated cost.</summary>
        /// <param name="costUsd">The call's estimated USD cost.</param>
        public void Accrue(decimal costUsd) => SpentUsd += costUsd;
    }
}
