using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Ingests news from every registered <see cref="INewsSource"/> into the <see cref="NewsRecord"/> store of record
/// (gh#358, R-2), deduped across sources. The news analogue of <see cref="BarBackfillService"/>: an overlapping
/// re-poll updates in place rather than appending, because the dedup key — not the writer — is the idempotence
/// guard.
/// </summary>
/// <remarks>
/// News is deliberately multi-source — the same story from Finnhub and Tiingo collapses to one row whose provenance
/// records both feeds. A source that has no news capability, or that throws, is logged and skipped so it cannot
/// cost the other sources their pass (mirrors the bar backfill's per-item guard). Relevance — mapping tickers to
/// instruments — is downstream (gh#359); this service stores the raw item.
/// </remarks>
public sealed class NewsIngestionService
{
    private const string NewsType = "news";

    private readonly TradingCopilotDbContext _database;
    private readonly IReadOnlyList<INewsSource> _sources;
    private readonly NewsIngestionOptions _options;
    private readonly ILogger<NewsIngestionService> _logger;

    /// <summary>Creates the service over the scoped database and the registered news sources.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="sources">Every registered news source; empty until a provider adapter is wired.</param>
    /// <param name="options">The poll cadence and lookback.</param>
    /// <param name="logger">The logger.</param>
    public NewsIngestionService(
        TradingCopilotDbContext database,
        IEnumerable<INewsSource> sources,
        IOptions<NewsIngestionOptions> options,
        ILogger<NewsIngestionService> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _sources = [.. sources];
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs one ingestion pass over every registered source.</summary>
    /// <param name="now">The instant the pass evaluates against.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many stored items were added or had their provenance updated.</returns>
    public async Task<int> IngestAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset since = now - TimeSpan.FromMinutes(_options.LookbackMinutes);

        // Fan in across sources, deduping as we go: the key maps to the item to store plus the set of feeds that
        // carried it (provenance). The first feed to carry a story wins its content; later feeds union into
        // provenance rather than producing a second row.
        Dictionary<string, PendingItem> deduped = new(StringComparer.Ordinal);

        foreach (INewsSource source in _sources)
        {
            string feed = source.Id.ToString();
            IReadOnlyList<NewsItem> items;
            try
            {
                items = await source.GetNewsAsync(since, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (VenueCapabilityNotSupportedException capability)
            {
                // A configuration truth, not a crash: this source simply offers no news (R-17).
                _logger.LogWarning(capability, "News skipped {Feed} — the source does not provide news.", feed);
                continue;
            }
            catch (Exception error)
            {
                _logger.LogError(error, "News fetch failed for {Feed}; the next pass retries.", feed);
                continue;
            }

            foreach (NewsItem item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Url))
                {
                    continue; // no URL, no dedup identity — drop rather than store an unmergeable row
                }

                string key = NewsDedupKey.For(item.Url);
                if (deduped.TryGetValue(key, out PendingItem? pending))
                {
                    pending.Feeds.Add(feed);
                }
                else
                {
                    deduped[key] = new PendingItem(item, [feed]);
                }
            }
        }

        if (deduped.Count == 0)
        {
            return 0;
        }

        // Load the overlap once, merge in memory — EF-first, one round trip, mirroring the bar upsert.
        List<string> keys = [.. deduped.Keys];
        Dictionary<string, NewsRecord> existing = await _database.News
            .Where(record => keys.Contains(record.DedupKey))
            .ToDictionaryAsync(record => record.DedupKey, cancellationToken);

        int written = 0;
        foreach ((string key, PendingItem pending) in deduped)
        {
            if (existing.TryGetValue(key, out NewsRecord? stored))
            {
                // The story is already stored; a re-poll from the same feed is a genuine no-op. Only a NEW feed
                // carrying it changes anything — union its provenance and re-stamp when we learned it.
                List<string> merged = [.. stored.SourceFeeds.Union(pending.Feeds, StringComparer.OrdinalIgnoreCase)];
                if (merged.Count != stored.SourceFeeds.Count)
                {
                    stored.SourceFeeds = merged;
                    stored.RecordedAt = now;
                    written++;
                }

                continue;
            }

            _database.News.Add(new NewsRecord
            {
                DedupKey = key,
                Type = NewsType,
                Url = pending.Item.Url,
                Title = pending.Item.Title,
                Summary = pending.Item.Summary,
                PublishedAt = pending.Item.PublishedAt.ToUniversalTime(),
                Tickers = [.. (pending.Item.Tickers ?? []).Distinct(StringComparer.OrdinalIgnoreCase)],
                SourceFeeds = [.. pending.Feeds],
                RecordedAt = now,
            });
            written++;
        }

        await _database.SaveChangesAsync(cancellationToken);
        return written;
    }

    private sealed record PendingItem(NewsItem Item, HashSet<string> Feeds);
}
