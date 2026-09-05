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
                else if (FindFuzzyMatch(deduped.Values, item, feed) is { } fuzzyPending)
                {
                    // R-2 fuzzy fallback, WITHIN the pass: a second feed carrying the same story under a DIFFERENT
                    // URL (Finnhub and Tiingo rarely agree on a canonical link) gets a different key, so canonical
                    // dedup missed it. Union its feed onto the first item's pending row rather than opening a second
                    // one -- the first feed to carry a story wins its content, exactly as the canonical path does.
                    // Cross-feed ONLY (FindFuzzyMatch skips candidates already carrying `feed`): two near-duplicate
                    // headlines from the SAME feed are distinct items (a follow-up or correction under a new URL), and
                    // unioning `feed` onto the first would be a HashSet no-op that silently drops the second (gh#764 /
                    // #827 review) -- those fall through to `else` and are stored as their own row. The merge folds in
                    // the second provider's tickers and a fuller headline, which each provider tags differently.
                    fuzzyPending.MergeFuzzy(item, feed);
                }
                else
                {
                    deduped[key] = new PendingItem(item, feed);
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
        // arriving. Bounded to the publication-time window this pass spans (± the fuzzy gap; PublishedAt is indexed),
        // then matched in memory below. Keep rows whose canonical key is ALSO in this pass: a later overlapping poll
        // normally carries the original URL again, and a different-URL copy can still need that stored row as its
        // fuzzy candidate when the current original's metadata (e.g. tickers) is incomplete (gh#764 / #827 review).
        // Exact-key pendings never call FindFuzzyStored, so including them cannot turn an exact match into a fuzzy one.
        //
        // Loaded only when at least one pending MISSED the canonical lookup, because that is the only condition
        // under which a candidate is ever consulted: FindFuzzyStored sits in the `else` after `existing` fails
        // below. On a typical pass every pending hits `existing`, and this query used to materialize roughly the
        // lookback plus a gap either side of stored rows as TRACKED entities for nothing (gh#836). Tracking is
        // needed on the rows that DO match, so AsNoTracking is not the answer — not asking is.
        List<NewsRecord> fuzzyCandidates = [];
        if (deduped.Keys.Any(key => !existing.ContainsKey(key)))
        {
            // The SAME configured gap the comparison uses (gh#836): sizing this window off the constant while the
            // comparison honoured a wider configured value would accept pairs this query never fetched, so the
            // knob would silently do nothing past its default.
            DateTimeOffset earliest = deduped.Values.Min(pending => pending.PublishedAt.ToUniversalTime());
            DateTimeOffset latest = deduped.Values.Max(pending => pending.PublishedAt.ToUniversalTime());
            DateTimeOffset windowStart = earliest - _options.FuzzyMaxPublishedGap;
            DateTimeOffset windowEnd = latest + _options.FuzzyMaxPublishedGap;
            fuzzyCandidates = await _database.News
                .Where(record => record.PublishedAt >= windowStart && record.PublishedAt <= windowEnd)
                .ToListAsync(cancellationToken);
        }

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
            // folds this pending's provenance, tickers and (if fuller) headline onto the surviving row rather than
            // storing a duplicate under the new URL (gh#764 / #827 review).
            if (FindFuzzyStored(fuzzyCandidates, pending) is { } fuzzy)
            {
                if (MergeFuzzyStored(fuzzy, pending, now))
                {
                    written++;
                }

                continue;
            }

            _database.News.Add(new NewsRecord
            {
                DedupKey = key,
                Type = NewsType,
                Url = pending.Url,
                Title = pending.Title,
                Summary = pending.Summary,
                PublishedAt = pending.PublishedAt.ToUniversalTime(),
                Tickers = [.. pending.Tickers],
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

    // The first pending item from a DIFFERENT feed that is likely the same story as `item` under the R-2 fuzzy rule
    // (title + time + ticker). Candidates already carrying `feed` are skipped: the fallback exists for cross-source
    // disagreement on the canonical URL, so a same-feed near-duplicate is a distinct item to store, not a merge target
    // (unioning `feed` onto it is a no-op that would silently drop the row -- gh#764 / #827 review).
    private PendingItem? FindFuzzyMatch(IEnumerable<PendingItem> candidates, NewsItem item, string feed)
    {
        foreach (PendingItem candidate in candidates)
        {
            if (candidate.Feeds.Contains(feed))
            {
                continue;
            }

            if (NewsFuzzyDedup.AreLikelyTheSameStory(
                candidate.Title, candidate.PublishedAt, candidate.Tickers,
                item.Title, item.PublishedAt, item.Tickers ?? [],
                _options.FuzzyMinTitleSimilarity, _options.FuzzyMaxPublishedGap))
            {
                return candidate;
            }
        }

        return null;
    }

    // The first stored row that is likely the same story as `pending` under the R-2 fuzzy rule. Mirrors
    // FindFuzzyMatch's same-feed skip across the poll boundary: a candidate that already carries one of
    // `pending`'s feeds is skipped, because a same-feed near-duplicate under a new URL is ordinarily a
    // follow-up or correction, not a duplicate to fold in (gh#1132).
    //
    // The mirror is deliberately naive, not the finer-grained "which feed's rendering is currently on the row"
    // read the asymmetry noted in gh#1132 would need: a stored row can carry SEVERAL feeds by the time it is
    // consulted (an earlier cross-feed fuzzy merge, or plain canonical corroboration), so "already carries this
    // feed" is a weaker signal here than it is against a single-feed PendingItem, and can occasionally refuse a
    // genuine later correction from a feed that only ever contributed provenance, not content. That refusal
    // costs an extra row for one story -- the SAME direction gh#764 / #827 already chose as the cheaper error:
    // a false positive here (a wrongly-merged row) destroys a real event, while a false negative (an unmerged
    // duplicate row) only costs a duplicate view of it. Tracking per-feed content provenance to close that gap
    // precisely is a bigger, data-model-shaped change (a "which feed authored the surviving Title" column) that
    // this Work Estimate 2 fix does not attempt -- flagged for the maintainer rather than silently narrowed.
    private NewsRecord? FindFuzzyStored(IEnumerable<NewsRecord> candidates, PendingItem pending)
    {
        foreach (NewsRecord candidate in candidates)
        {
            if (candidate.SourceFeeds.Any(sourceFeed => pending.Feeds.Contains(sourceFeed, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (NewsFuzzyDedup.AreLikelyTheSameStory(
                candidate.Title, candidate.PublishedAt, candidate.Tickers,
                pending.Title, pending.PublishedAt, pending.Tickers,
                _options.FuzzyMinTitleSimilarity, _options.FuzzyMaxPublishedGap))
            {
                return candidate;
            }
        }

        return null;
    }

    // A cross-pass FUZZY match onto an already-stored row: two providers' renderings of one story under different
    // URLs, so union both the provenance AND the tickers (each provider tags its own -- gh#359 maps relevance off
    // Tickers), and adopt the incoming headline when it is a strictly fuller rendering. Distinct from UnionProvenance,
    // the canonical-URL path, where the same payload was already resolved and only provenance can grow (#827 review).
    private static bool MergeFuzzyStored(NewsRecord stored, PendingItem pending, DateTimeOffset now)
    {
        List<string> feeds = [.. stored.SourceFeeds.Union(pending.Feeds, StringComparer.OrdinalIgnoreCase)];
        List<string> tickers = [.. stored.Tickers.Union(pending.Tickers, StringComparer.OrdinalIgnoreCase)];
        bool fuller = NewsFuzzyDedup.IsFullerRendering(stored.Title, pending.Title);

        if (feeds.Count == stored.SourceFeeds.Count && tickers.Count == stored.Tickers.Count && !fuller)
        {
            return false;
        }

        stored.SourceFeeds = feeds;
        stored.Tickers = tickers;
        if (fuller)
        {
            stored.Title = pending.Title;
            stored.Summary = pending.Summary;
        }

        stored.RecordedAt = now;
        return true;
    }

    // The accumulating dedup state for one pending row: the winning content plus the provenance, tickers and any
    // fuller headline folded in from a later feed that matches it. Mutable because a cross-feed fuzzy match merges
    // metadata in place (#827 review); the canonical exact-URL path only ever grows Feeds.
    private sealed class PendingItem
    {
        public PendingItem(NewsItem item, string feed)
        {
            Url = item.Url!;
            Title = item.Title;
            Summary = item.Summary;
            PublishedAt = item.PublishedAt;
            Tickers = new HashSet<string>(item.Tickers ?? [], StringComparer.OrdinalIgnoreCase);
            Feeds = [feed];
        }

        public string Url { get; }

        public string Title { get; private set; }

        public string Summary { get; private set; }

        public DateTimeOffset PublishedAt { get; }

        public HashSet<string> Tickers { get; }

        public HashSet<string> Feeds { get; }

        // Fold a cross-feed fuzzy match in: union its feed and tickers, and adopt its headline when it is a strictly
        // fuller rendering of ours. A same-feed candidate never reaches here (FindFuzzyMatch skips it).
        public void MergeFuzzy(NewsItem other, string feed)
        {
            Feeds.Add(feed);
            Tickers.UnionWith(other.Tickers ?? []);
            if (NewsFuzzyDedup.IsFullerRendering(Title, other.Title))
            {
                Title = other.Title;
                Summary = other.Summary;
            }
        }
    }
}
