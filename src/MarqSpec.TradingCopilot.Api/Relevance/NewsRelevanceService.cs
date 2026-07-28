using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
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

    private readonly TradingCopilotDbContext _database;
    private readonly ILogger<NewsRelevanceService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="logger">The logger.</param>
    public NewsRelevanceService(TradingCopilotDbContext database, ILogger<NewsRelevanceService> logger)
    {
        _database = database;
        _logger = logger;
    }

    /// <summary>Runs one resolution pass over the news that needs it.</summary>
    /// <param name="now">The instant to stamp resolution with.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many news items were resolved.</returns>
    public async Task<int> ResolveAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset configChangedAt = await _database.RelevanceConfigStates
            .Select(state => (DateTimeOffset?)state.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken) ?? DateTimeOffset.MinValue;

        // Load the (small) global config once and map to the pure resolver's domain shape.
        List<TickerInstrumentMapping> maps = [.. (await _database.TickerInstrumentMaps.AsNoTracking().ToListAsync(cancellationToken))
            .Select(map => new TickerInstrumentMapping(map.Ticker, map.Instrument))];
        List<NewsTopicDefinition> topics = [.. (await _database.NewsTopics.AsNoTracking().ToListAsync(cancellationToken))
            .Select(topic => new NewsTopicDefinition(topic.Name, topic.Keywords, topic.Scope, topic.Instrument))];

        // Page the work: a config change marks ALL prior news stale, and loading the whole history into one
        // SaveChanges would blow memory and eventually time out -- and then never make progress. Resolved rows
        // drop out of the predicate, so each page resolves a fresh set and progress is guaranteed.
        int total = 0;
        while (true)
        {
            List<NewsRecord> batch = await _database.News
                .Where(news => news.RelevanceResolvedAt == null || news.RelevanceResolvedAt < configChangedAt)
                .OrderBy(news => news.PublishedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (NewsRecord news in batch)
            {
                RelevanceMatch match = NewsRelevanceResolver.Resolve(
                    news.Tickers, $"{news.Title} {news.Summary}", maps, topics);
                news.MatchedInstruments = [.. match.Instruments];
                news.MatchedTopics = [.. match.Topics];
                news.RelevanceResolvedAt = now;
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
}
