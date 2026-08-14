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
                else if (FindFuzzyMatch(deduped.Values, item) is { } fuzzyPending)
                {
                    // R-2 fuzzy fallback, WITHIN the pass: a second feed carrying the same story under a DIFFERENT
                    // URL (Finnhub and Tiingo rarely agree on a canonical link) gets a different key, so canonical
                    // dedup missed it. Union its feed onto the first item's pending row rather than opening a second
                    // one -- the first feed to carry a story wins its content, exactly as the canonical path does.
                    fuzzyPending.Feeds.Add(feed);
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

        // R-2 fuzzy fallback, ACROSS passes: a story already stored under a DIFFERENT canonical URL than the one now
        // arriving. Bounded to the publication-time window this pass spans (± the fuzzy gap; PublishedAt is indexed)
        // and to rows NOT already exact-matched above, then matched in memory below. Empty when nothing recent is
        // stored, so the ordinary first-time-seen path pays only the bounded window read.
        DateTimeOffset earliest = deduped.Values.Min(pending => pending.Item.PublishedAt.ToUniversalTime());
        DateTimeOffset latest = deduped.Values.Max(pending => pending.Item.PublishedAt.ToUniversalTime());
        DateTimeOffset windowStart = earliest - NewsFuzzyDedup.MaxPublishedGap;
        DateTimeOffset windowEnd = latest + NewsFuzzyDedup.MaxPublishedGap;
        List<NewsRecord> fuzzyCandidates = await _database.News
            .Where(record => record.PublishedAt >= windowStart && record.PublishedAt <= windowEnd
                && !keys.Contains(record.DedupKey))
            .ToListAsync(cancellationToken);

        int written = 0;
        foreach ((string key, PendingItem pending) in deduped)
        {
            if (existing.TryGetValue(key, out NewsRecord? stored))
            {
                // The story is already stored under this canonical key; a re-poll from the same feed is a genuine
                // no-op. Only a NEW feed carrying it changes anything — union its provenance and re-stamp.
                if (UnionProvenance(stored, pending.Feeds, now))
                {
                    written++;
                }

                continue;
            }

            // Canonical matching found nothing — try the R-2 fuzzy fallback against recently-stored rows. A match
            // unions provenance onto the surviving row rather than storing a duplicate under the new URL (gh#764).
            if (FindFuzzyStored(fuzzyCandidates, pending.Item) is { } fuzzy)
            {
                if (UnionProvenance(fuzzy, pending.Feeds, now))
                {
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

    // Unions `feeds` into a stored row's provenance, re-stamping RecordedAt when it actually grew. Returns whether it
    // changed -- so a re-poll that adds no new feed is the no-op it should be.
    private static bool UnionProvenance(NewsRecord stored, IEnumerable<string> feeds, DateTimeOffset now)
    {
        List<string> merged = [.. stored.SourceFeeds.Union(feeds, StringComparer.OrdinalIgnoreCase)];
        if (merged.Count == stored.SourceFeeds.Count)
        {
            return false;
        }

        stored.SourceFeeds = merged;
        stored.RecordedAt = now;
        return true;
    }

    // The first pending item that is likely the same story as `item` under the R-2 fuzzy rule (title + time + ticker).
    private static PendingItem? FindFuzzyMatch(IEnumerable<PendingItem> candidates, NewsItem item)
    {
        foreach (PendingItem candidate in candidates)
        {
            if (NewsFuzzyDedup.AreLikelyTheSameStory(
                candidate.Item.Title, candidate.Item.PublishedAt, candidate.Item.Tickers ?? [],
                item.Title, item.PublishedAt, item.Tickers ?? []))
            {
                return candidate;
            }
        }

        return null;
    }

    // The first stored row that is likely the same story as `item` under the R-2 fuzzy rule.
    private static NewsRecord? FindFuzzyStored(IEnumerable<NewsRecord> candidates, NewsItem item)
    {
        foreach (NewsRecord candidate in candidates)
        {
            if (NewsFuzzyDedup.AreLikelyTheSameStory(
                candidate.Title, candidate.PublishedAt, candidate.Tickers,
                item.Title, item.PublishedAt, item.Tickers ?? []))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed record PendingItem(NewsItem Item, HashSet<string> Feeds);
}
