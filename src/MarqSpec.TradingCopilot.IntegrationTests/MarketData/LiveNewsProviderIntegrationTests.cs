using System.Net;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost.LiveProvider;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace MarqSpec.TradingCopilot.IntegrationTests.MarketData;

/// <summary>
/// Independent QA for the news pipeline against the <b>real</b> Finnhub and Tiingo APIs (gh#1122, of gh#383) —
/// the live half gh#360 could only stub and gh#464 deferred to a staging environment that does not exist.
/// Live-provider tier: real outbound provider calls, no deployed application, the store a throwaway Postgres
/// container.
/// </summary>
/// <remarks>
/// <para>
/// Written from the spec (gh#383 acceptance, R-2 multi-source news, R-17 data-only provider, the data-dictionary
/// <c>SoftSignal (NewsItem)</c> row), not from the adapter implementations.
/// </para>
/// <para>
/// <b>Expected values are computed independently of production.</b> The suite reads the raw <see cref="NewsItem"/>s
/// from each <see cref="INewsSource"/> itself and derives what the store ought to contain from those, never by
/// calling <c>NewsDedupKey</c> — a suite that asked production for the answer could not catch a dedup that only
/// matches exact strings, which is the regression this tier exists to guard.
/// </para>
/// <para>
/// <b>What this tier found on its first run (2026-09-05), and what it therefore cannot yet prove.</b> Tiingo's
/// plan does not include the News API, so every Tiingo news call returns <c>403</c> (gh#1125) — the key is valid,
/// the entitlement is not. R-2's cross-source dedup consequently has <i>one</i> live feed, and the case that
/// witnesses two feeds collapsing to one row is marked blocked against that issue rather than shipped as a silent
/// skip. Finnhub's own free tier is live and is asserted here in full. Two further findings are filed, not fixed
/// (QA contract §3): gh#1123 (the shipped 60-minute lookback admits none of Finnhub's articles) and gh#1124 (its
/// general category carries no tickers, so relevance resolution has no input).
/// </para>
/// </remarks>
[Trait("Category", "LiveProvider")]
public sealed class LiveNewsProviderIntegrationTests : IClassFixture<LiveNewsProviderFactory>
{
    private readonly LiveNewsProviderFactory _factory;
    private readonly ITestOutputHelper _output;

    public LiveNewsProviderIntegrationTests(LiveNewsProviderFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        ClearNewsAsync(_factory).GetAwaiter().GetResult();
    }

    // --- The live fan-in, for the feed that is actually entitled (gh#464 case 1, Finnhub half) ---

    [LiveNewsProviderFact]
    public async Task Finnhub_ShouldLandItsNewsOnALivePoll()
    {
        // FAILURE MODE: the adapter maps nothing out of a live payload — a renamed field, the epoch/ISO date
        // difference the two providers genuinely have, or an over-eager `since` filter — and ingestion quietly
        // stores zero while reporting a successful pass. Every stubbed test still passes; only a live pull sees
        // it. (This is not hypothetical: at the SHIPPED lookback it stores zero — gh#1123.)
        RawSnapshot raw = await ReadRawFeedsAsync(_factory);
        await IngestAsync(_factory);

        List<NewsRecord> rows = await ReadAllAsync(_factory);

        raw.ItemsFor("finnhub").Should().NotBeEmpty(
            "Finnhub served nothing over a {0}-minute window", LiveNewsProviderFactory.LookbackMinutes);
        rows.Where(row => row.SourceFeeds.Contains("finnhub")).Should()
            .NotBeEmpty("Finnhub served {0} items, so the store must not be empty", raw.ItemsFor("finnhub").Count);

        // The mapped shape the rest of R-2 reads, asserted on real payloads rather than fabricated ones.
        rows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.Url), "a URL is the dedup identity");
        rows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.Title), "an untitled story is unreadable");
        rows.Should().OnlyContain(row => row.PublishedAt != default, "an unmapped timestamp defeats the lookback");
    }

    // --- Distinct live stories must stay distinct (gh#464 case 3) ---

    [LiveNewsProviderFact]
    public async Task DistinctLiveStories_ShouldEachKeepTheirOwnRow()
    {
        // FAILURE MODE: over-collapse. The R-2 fuzzy fallback merges near-identical headlines, and it is
        // cross-feed BY DESIGN — two similar headlines from the SAME feed are distinct stories (a follow-up or a
        // correction under a new URL), and merging them would silently delete a real event from the desk's view.
        // Real newswire copy is where that bites: a live Finnhub pull carries genuinely similar Reuters and CNBC
        // headlines about one event, which fabricated fixtures never do convincingly.
        RawSnapshot raw = await ReadRawFeedsAsync(_factory);
        int distinctFinnhubUrls = raw.DistinctUrlsFor("finnhub").Count;

        await IngestAsync(_factory);
        List<NewsRecord> rows = await ReadAllAsync(_factory);

        _output.WriteLine(raw.Describe());

        // Only Finnhub is entitled today (gh#1125), so every distinct URL it served must own exactly one row —
        // no merging within a feed, and no duplication either. An exact equality, in both directions.
        rows.Should().HaveCount(
            distinctFinnhubUrls,
            "Finnhub served {0} distinct URLs and nothing may merge within a single feed or duplicate across passes",
            distinctFinnhubUrls);
    }

    // --- A refused provider must not sink the other (gh#464 case 4) ---

    [LiveNewsProviderFact]
    public async Task ARefusedProvider_ShouldNotSinkTheOther()
    {
        // FAILURE MODE: one provider rejecting the request aborts the whole pass, so a single bad key or a 429
        // costs the desk ALL news rather than one feed's share. Proven against a real provider refusal, not a
        // stubbed throw — the gh#358 guard catches `Exception`, and only a live call shows what the client
        // actually throws from inside (here an HttpRequestException out of EnsureSuccessStatusCode).
        LiveNewsProviderTiingoOutageFactory outage = new();
        await ((IAsyncLifetime)outage).InitializeAsync();
        try
        {
            await ClearNewsAsync(outage);

            await IngestAsync(outage);

            List<NewsRecord> rows = await ReadAllAsync(outage);
            rows.Where(row => row.SourceFeeds.Contains("finnhub")).Should()
                .NotBeEmpty("Finnhub is healthy and must still land its news while Tiingo is rejected");
            rows.Should().OnlyContain(
                row => !row.SourceFeeds.Contains("tiingo"),
                "the rejected provider contributed nothing, so no row may carry its provenance");
        }
        finally
        {
            await ((IAsyncLifetime)outage).DisposeAsync();
        }
    }

    // --- Cross-source collapse: blocked, and blocked LOUDLY (gh#464 case 2) ---

    [Fact(Skip = "blocked by gh#1125 — Tiingo's plan excludes the News API (403 'You do not have permission to "
        + "access the News API'), so there is no second live feed and a cross-source collapse cannot be witnessed "
        + "by anything, on any environment. Deliberately a declared block rather than a passing test: a suite "
        + "that reports coverage it does not have is worse than no suite (PR #1014 / #1013).")]
    public Task SameStory_ShouldCollapseToOneRecord_AcrossLiveProviders() =>
        throw new NotImplementedException(
            "Unblock with gh#1125, then assert: a URL both feeds carry becomes exactly one NewsRecord whose "
            + "SourceFeeds contains finnhub and tiingo. The raw-snapshot helper below already computes the "
            + "cross-feed overlap set this needs.");

    // --- Credentials come from configuration and nowhere else (gh#464 case 5) ---

    [Fact]
    public async Task Credentials_ShouldResolveFromConfigOnly()
    {
        // BY CONSTRUCTION, and needs no credentials — so it runs on every PR, not only in this tier.
        //
        // FAILURE MODE: a token embedded in source, a default baked into an adapter, or a client falling back to
        // some ambient credential. Any of those lets a host configured with NO keys still reach a provider.
        // Grepping the tree for a literal token could not catch a key assembled at runtime; asserting the
        // OUTCOME can.
        KeylessNewsProviderFactory keyless = new();
        await ((IAsyncLifetime)keyless).InitializeAsync();
        try
        {
            await using (AsyncServiceScope scope = keyless.Services.CreateAsyncScope())
            {
                scope.ServiceProvider.GetServices<INewsSource>().Should()
                    .BeEmpty("with no key configured the application must register no news source at all");
            }

            int written = await IngestAsync(keyless);

            written.Should().Be(0);
            (await ReadAllAsync(keyless)).Should().BeEmpty(
                "a host with no configured credentials must produce no news from anywhere");
        }
        finally
        {
            await ((IAsyncLifetime)keyless).DisposeAsync();
        }
    }

    // --- Tiingo's entitlement gap, pinned rather than blessed (QA contract, guard discipline §2) ---

    [LiveNewsProviderFact]
    public async Task Tiingo_ShouldStillBeRefusedByItsPlan_PinningTheGapUntilGh1125()
    {
        // PINS OBSERVED BEHAVIOUR, gh#1125: the configured Tiingo token is valid (/api/test returns 200) but its
        // plan does not include the News API, so every news call is refused. Asserting it keeps "R-2 has one live
        // feed" visible in CI instead of tribal knowledge — and this test goes RED the moment the entitlement is
        // bought, which is exactly the prompt to promote the blocked cross-source case above.
        RawSnapshot raw = await ReadRawFeedsAsync(_factory);

        raw.FailureFor("tiingo").Should().NotBeNull(
            "Tiingo's plan excludes the News API (gh#1125); if this is now null the entitlement exists and "
            + "SameStory_ShouldCollapseToOneRecord_AcrossLiveProviders should be unblocked");
        raw.FailureFor("tiingo")!.Message.Should().Contain(
            ((int)HttpStatusCode.Forbidden).ToString(),
            "the refusal is an HTTP 403 from the provider, not a transport or parsing failure");
    }

    // --- Free-tier data quality (the gh#383 "Open" item) ---

    [LiveNewsProviderFact]
    public async Task FreeTierFeeds_ShouldReportTheirDataQuality()
    {
        // Not a judgement on the providers' editorial quality — it asserts the SHAPE the pipeline depends on and
        // reports the rest as evidence, so free-tier usability stops being an open question answered by nobody.
        // Engineering "Data sources" flags Finnhub free-tier quality as unverified; this is the first look, and
        // it is where gh#1123 (lookback) and gh#1124 (no tickers) came from.
        RawSnapshot raw = await ReadRawFeedsAsync(_factory);
        _output.WriteLine(raw.Describe());

        IReadOnlyList<NewsItem> finnhub = raw.ItemsFor("finnhub");
        finnhub.Should().NotBeEmpty("a free tier that serves nothing is not a usable source");
        finnhub.Should().OnlyContain(
            item => !string.IsNullOrWhiteSpace(item.Url),
            "finnhub: a URL is the dedup identity, so an item without one cannot be stored at all");
        finnhub.Should().OnlyContain(
            item => !string.IsNullOrWhiteSpace(item.Title),
            "finnhub: an untitled story is unreadable in the blotter");
        finnhub.Should().OnlyContain(
            item => item.PublishedAt < DateTimeOffset.UtcNow.AddHours(1),
            "finnhub: a publish time in the future means the timestamp is misparsed");
        finnhub.Should().OnlyContain(
            item => item.PublishedAt > DateTimeOffset.UtcNow.AddDays(-30),
            "finnhub: a publish time far outside the lookback means the timestamp is misparsed");

        // DEFECT gh#1124: the general category tags no tickers, so gh#359 relevance resolution has no input.
        // Pinned as observed, not blessed — it flips into that issue's regression guard when a tagged feed lands.
        finnhub.Should().OnlyContain(
            item => item.Tickers.Count == 0,
            "gh#1124 pins that Finnhub's general category carries NO tickers; tickers appearing here means the "
            + "gap has closed and gh#1124 should be revisited");
    }

    // --- Helpers ---

    private static async Task<int> IngestAsync(LiveNewsProviderFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NewsIngestionService service = scope.ServiceProvider.GetRequiredService<NewsIngestionService>();
        return await service.IngestAsync(DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static async Task<List<NewsRecord>> ReadAllAsync(LiveNewsProviderFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.News.AsNoTracking().ToListAsync();
    }

    private static async Task ClearNewsAsync(LiveNewsProviderFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.News.ExecuteDeleteAsync();
    }

    // Reads every registered source directly, recording what each one SERVED or how it FAILED — the independent
    // basis for every expectation above. A throwing source is captured rather than propagated, because "one feed
    // refused us" is a fact this suite reports on, not a reason it cannot run.
    private static async Task<RawSnapshot> ReadRawFeedsAsync(LiveNewsProviderFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        DateTimeOffset since = DateTimeOffset.UtcNow.AddMinutes(-LiveNewsProviderFactory.LookbackMinutes);

        Dictionary<string, IReadOnlyList<NewsItem>> served = new(StringComparer.Ordinal);
        Dictionary<string, Exception> refused = new(StringComparer.Ordinal);

        foreach (INewsSource source in scope.ServiceProvider.GetServices<INewsSource>())
        {
            string feed = source.Id.ToString();
            try
            {
                served[feed] = await source.GetNewsAsync(since, CancellationToken.None);
            }
            catch (Exception error)
            {
                refused[feed] = error;
            }
        }

        return new RawSnapshot(served, refused);
    }

    private sealed class RawSnapshot(
        Dictionary<string, IReadOnlyList<NewsItem>> served,
        Dictionary<string, Exception> refused)
    {
        public IReadOnlyList<NewsItem> ItemsFor(string feed) =>
            served.TryGetValue(feed, out IReadOnlyList<NewsItem>? items) ? items : [];

        public Exception? FailureFor(string feed) =>
            refused.TryGetValue(feed, out Exception? error) ? error : null;

        public HashSet<string> DistinctUrlsFor(string feed) =>
            [.. ItemsFor(feed).Select(Normalize)];

        // A URL appearing in more than one feed's payload — the only place a cross-source collapse can be
        // witnessed by exact identity. Empty while gh#1125 leaves one feed refused; kept because it is exactly
        // what the blocked case needs the day that changes.
        public IReadOnlyList<string> UrlsCarriedByMoreThanOneFeed =>
        [
            .. served
                .SelectMany(feed => feed.Value.Select(item => (Feed: feed.Key, Url: Normalize(item))))
                .GroupBy(pair => pair.Url)
                .Where(group => group.Select(pair => pair.Feed).Distinct().Count() > 1)
                .Select(group => group.Key)
        ];

        public string Describe()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<string> lines =
            [
                $"FREE-TIER DATA QUALITY (gh#1122) — lookback {LiveNewsProviderFactory.LookbackMinutes} min, "
                + $"observed {now:u}",
            ];

            foreach ((string feed, IReadOnlyList<NewsItem> items) in served)
            {
                lines.Add(
                    $"  {feed}: {items.Count} items, {DistinctUrlsFor(feed).Count} distinct URLs, "
                    + $"{items.Count(item => item.Tickers.Count > 0)} with tickers");

                // The age profile gh#1123 came from: how much of a live payload each candidate lookback admits.
                foreach (int window in (int[])[LiveNewsProviderFactory.ProductionDefaultLookbackMinutes, 240, 1440])
                {
                    int inside = items.Count(item => item.PublishedAt > now.AddMinutes(-window));
                    string note = window == LiveNewsProviderFactory.ProductionDefaultLookbackMinutes
                        ? "  <-- the SHIPPED default (gh#1123)"
                        : string.Empty;
                    lines.Add($"    within {window,5} min: {inside,4}{note}");
                }
            }

            foreach ((string feed, Exception error) in refused)
            {
                lines.Add($"  {feed}: REFUSED — {error.Message}");
            }

            lines.Add($"  URLs carried by more than one feed: {UrlsCarriedByMoreThanOneFeed.Count}");
            return string.Join(Environment.NewLine, lines);
        }

        private static string Normalize(NewsItem item) => item.Url.TrimEnd('/').ToLowerInvariant();
    }
}
