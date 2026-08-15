using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Relevance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Relevance;

/// <summary>
/// Resolves news relevance (gh#359, R-2): applies the deployment's global ticker↔instrument maps and topics to each
/// news item, materializing its matched instruments and topics onto the (global) <see cref="NewsRecord"/>. A pure
/// mapping layer over the deterministic <see cref="NewsRelevanceResolver"/> — no LLM. The per-user salience that
/// reweights these matches is a separate concern (gh#27).
/// </summary>
/// <remarks>
/// The pass resolves news that is <b>unresolved</b> (never seen) or <b>stale</b> — resolved before the relevance
/// config last changed (tracked by the single-row <see cref="RelevanceConfigState"/>). So a config edit re-resolves
/// exactly the affected news, predictably, without touching every row at edit time. Resolution is idempotent:
/// re-running against unchanged config produces the same matched set.
/// </remarks>
public sealed class NewsRelevanceService
{
    private const int BatchSize = 500;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<float>> _noVectors =
        new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);

    private readonly TradingCopilotDbContext _database;
    private readonly IEmbeddingProvider _provider;
    private readonly INewsEmbeddingSimilarity _similarity;
    private readonly ILogger<NewsRelevanceService> _logger;

    /// <summary>Creates the service over the scoped database and the embedding read seam.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="provider">The embedding provider — its <see cref="IEmbeddingProvider.IsAvailable"/> gates the semantic reads.</param>
    /// <param name="similarity">The stored-vector read seam (gh#852) — the real pgvector read in production, a fake in unit tests.</param>
    /// <param name="logger">The logger.</param>
    public NewsRelevanceService(
        TradingCopilotDbContext database,
        IEmbeddingProvider provider,
        INewsEmbeddingSimilarity similarity,
        ILogger<NewsRelevanceService> logger)
    {
        _database = database;
        _provider = provider;
        _similarity = similarity;
        _logger = logger;
    }

    /// <summary>Runs one resolution pass over the news that needs it.</summary>
    /// <param name="now">The instant to stamp resolution with.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many news items were resolved.</returns>
    public async Task<int> ResolveAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The monotonic config generation (gh#418). Read once, before the maps/topics below, so a config edit that
        // commits mid-pass is stamped conservatively: we resolve against the config we read and stamp the version
        // we read, so the next pass sees a lower version than the now-current config and re-resolves. No clocks,
        // so nothing to skew.
        long configVersion = await _database.RelevanceConfigStates
            .Select(state => (long?)state.Version)
            .FirstOrDefaultAsync(cancellationToken) ?? 0L;

        // Load the (small) global config once and map to the pure resolver's domain shape.
        List<TickerInstrumentMapping> maps = [.. (await _database.TickerInstrumentMaps.AsNoTracking().ToListAsync(cancellationToken))
            .Select(map => new TickerInstrumentMapping(map.Ticker, map.Instrument))];
        List<NewsTopicDefinition> topics = [.. (await _database.NewsTopics.AsNoTracking().ToListAsync(cancellationToken))
            .Select(topic => new NewsTopicDefinition(topic.Name, topic.Keywords, topic.Scope, topic.Instrument))];

        // Attach each topic's stored vector once per pass (topics are global + small). A provider that is down, or a
        // read fault, leaves the topics un-embedded so matching degrades to keyword-only (gh#854).
        IReadOnlyList<NewsTopicDefinition> embeddedTopics = await WithTopicEmbeddingsAsync(topics, cancellationToken);

        // Page the work: a config change marks ALL prior news stale, and loading the whole history into one
        // SaveChanges would blow memory and eventually time out -- and then never make progress. Resolved rows
        // drop out of the predicate, so each page resolves a fresh set and progress is guaranteed.
        int total = 0;
        while (true)
        {
            List<NewsRecord> batch = await _database.News
                .Where(news => news.RelevanceVersion == null || news.RelevanceVersion < configVersion)
                .OrderBy(news => news.PublishedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            // The page's news vectors in ONE read (never one per item). A provider that is down, or a read fault,
            // yields no vectors so each item resolves keyword-only (gh#854).
            IReadOnlyDictionary<string, IReadOnlyList<float>> newsVectors =
                await ReadNewsVectorsAsync([.. batch.Select(news => news.DedupKey)], cancellationToken);

            foreach (NewsRecord news in batch)
            {
                RelevanceMatch match = NewsRelevanceResolver.Resolve(
                    news.Tickers, $"{news.Title} {news.Summary}", maps, embeddedTopics,
                    newsVectors.GetValueOrDefault(news.DedupKey));
                news.MatchedInstruments = [.. match.Instruments];
                news.MatchedTopics = [.. match.Topics];
                news.RelevanceVersion = configVersion; // the staleness authority (gh#418)
                news.RelevanceResolvedAt = now;        // observability timestamp only
            }

            await _database.SaveChangesAsync(cancellationToken);
            _database.ChangeTracker.Clear(); // release the page before loading the next
            total += batch.Count;

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            _logger.LogDebug("Relevance resolved for {Count} news item(s).", total);
        }

        return total;
    }

    // Reads each topic's stored vector once per pass and attaches it to the topic definition, so the resolver can
    // match a news item to a topic by embedding proximity. A provider that is down short-circuits before any read; a
    // read fault (or an internal-token timeout) degrades to un-embedded topics (keyword-only). Only a genuine caller
    // cancellation propagates -- the gh#589 discriminator, mirroring SemanticSalienceAxis.
    private async Task<IReadOnlyList<NewsTopicDefinition>> WithTopicEmbeddingsAsync(
        IReadOnlyList<NewsTopicDefinition> topics, CancellationToken cancellationToken)
    {
        if (!_provider.IsAvailable || topics.Count == 0)
        {
            return topics;
        }

        try
        {
            IReadOnlyList<StoredEmbedding> vectors =
                await _similarity.GetTopicVectorsAsync([.. topics.Select(topic => topic.Name)], cancellationToken);
            if (vectors.Count == 0)
            {
                return topics; // nothing embedded yet -> keyword-only
            }

            // Last-wins on OwnerId, tolerating multiple coexisting rows per owner after a model change (the store keys
            // on (OwnerKind, OwnerId, Model), so both models' vectors persist until swept) -- a ToDictionary would
            // throw on the duplicate key and degrade the whole pass. Mirrors gh#853's SemanticSalienceAxis, which
            // collapses the same way.
            Dictionary<string, IReadOnlyList<float>> byName = new(StringComparer.Ordinal);
            foreach (StoredEmbedding vector in vectors)
            {
                byName[vector.OwnerId] = vector.Vector;
            }

            return [.. topics.Select(topic =>
                byName.TryGetValue(topic.Name, out IReadOnlyList<float>? vector) ? topic with { Embedding = vector } : topic)];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // a genuine caller cancellation is host shutdown, not a read fault to swallow
        }
        catch (Exception error)
        {
            _logger.LogWarning(
                error, "Topic embedding read failed; relevance degrades to keyword-only topic matching for this pass.");
            return topics;
        }
    }

    // Reads the given news items' stored vectors in one seam call, as a dedupKey -> vector map. Same degrade posture
    // as the topic read: unavailable or empty short-circuits before any read, a fault degrades to no vectors
    // (keyword-only for the page), and only a genuine caller cancellation propagates.
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<float>>> ReadNewsVectorsAsync(
        IReadOnlyCollection<string> dedupKeys, CancellationToken cancellationToken)
    {
        if (!_provider.IsAvailable || dedupKeys.Count == 0)
        {
            return _noVectors;
        }

        try
        {
            IReadOnlyList<StoredEmbedding> vectors = await _similarity.GetVectorsAsync(dedupKeys, cancellationToken);

            // Last-wins on OwnerId (see WithTopicEmbeddingsAsync): tolerate coexisting per-model rows rather than
            // throwing on a duplicate key and degrading the whole page to keyword-only.
            Dictionary<string, IReadOnlyList<float>> byKey = new(StringComparer.Ordinal);
            foreach (StoredEmbedding vector in vectors)
            {
                byKey[vector.OwnerId] = vector.Vector;
            }

            return byKey;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "News vector read failed; relevance degrades to keyword-only for this page.");
            return _noVectors;
        }
    }
}
