using System.Net;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost.LiveProvider;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using static MarqSpec.TradingCopilot.IntegrationTests.TestHost.LiveProvider.FinnhubWireProbe;

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
/// <b>Expectations come off the wire, not out of the adapter</b> (<see cref="FinnhubWireProbe"/>). Asking the
/// registered <c>INewsSource</c> what the store should contain would make the component under test its own
/// oracle — a defect there moves expectation and outcome together and nothing ever fails. Both the inverted
/// lookback filter and the dropped blank-summary article were injected and passed unnoticed under an
/// adapter-derived expectation before this was rewritten to read the provider directly.
/// </para>
/// <para>
/// <b>What this tier found on its first run (2026-09-05), and what it therefore cannot yet prove.</b> Tiingo's
/// plan does not include the News API, so every Tiingo news call returns <c>403</c> (gh#1125) — the key is valid,
/// the entitlement is not. R-2's cross-source dedup consequently has <i>one</i> live feed, and the case that
/// witnesses two feeds collapsing to one row is a declared block against that issue rather than a silent skip.
/// Two further findings are filed, not fixed (QA contract §3): gh#1123 (the shipped 60-minute lookback admits
/// roughly 0–1% of Finnhub's articles — measured twice, 0 of 100 and then 1 of 100) and gh#1124 (its general
/// category carries no tickers, so relevance resolution has no input).
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

    // --- Everything the provider served reaches the store, and nothing else does (gh#464 cases 1 and 3) ---

    [LiveNewsProviderFact]
    public async Task EveryStoryTheProviderServed_ShouldReachTheStoreOfRecord()
    {
        // FAILURE MODE: a silent drop between provider and store — an over-eager validation rejecting a field the
        // live feed legitimately leaves blank (one article in a typical payload carries no summary), a mapping
        // guard swallowing items, a renamed field, or the epoch/ISO date difference the two providers genuinely
        // have. Ingestion reports a successful pass either way; only comparing against the provider's own payload
        // shows the gap. Two mirror directions matter as much: a row the provider never served would mean the
        // store is inventing stories, and a story stored TWICE across an overlapping re-poll would mean the
        // dedup key has stopped being the idempotence guard the whole design rests on.
        DateTimeOffset since = DateTimeOffset.UtcNow.AddMinutes(-LiveNewsProviderFactory.LookbackMinutes);
        string apiKey = LiveProviderConfig.FinnhubApiKey!;

        // Probed either side of the pass so that an article arriving mid-run cannot flake the assertion: what
        // appears in BOTH probes was stable throughout and must be stored; the union bounds what may legitimately
        // have been stored.
        IReadOnlyList<WireArticle> before = await FetchAsync(apiKey);
        await IngestAsync(_factory);

        // A SECOND pass over the same window. The dedup key is the idempotence guard, so an overlapping re-poll
        // must update in place and add nothing — and running it is what makes the duplication direction
        // reachable at all: after a single pass, a set of stored URLs cannot exhibit a duplicate row.
        await IngestAsync(_factory);
        IReadOnlyList<WireArticle> after = await FetchAsync(apiKey);

        List<NewsRecord> rows = await ReadAllAsync(_factory);
        HashSet<string> stored = [.. rows.Select(row => Normalize(row.Url))];

        // Grouped rather than compared raw: the pipeline is entitled to collapse URLs differing only by scheme,
        // `www.`, a trailing slash or a tracking parameter into ONE row carrying the FIRST one's raw URL.
        // Comparing raw strings would report the other form as a dropped story -- a red on correct behaviour --
        // so `Normalize` collapses those same axes (independently reimplemented, never NewsDedupKey) and each
        // stable group need only be REPRESENTED in the store.
        Dictionary<string, List<string>> beforeGroups = GroupInWindow(before, since);
        Dictionary<string, List<string>> afterGroups = GroupInWindow(after, since);
        List<string> stableKeys = [.. beforeGroups.Keys.Intersect(afterGroups.Keys)];
        HashSet<string> mayBeStored = [.. before.Concat(after).Select(article => Normalize(article.Url))];

        stableKeys.Should().NotBeEmpty(
            "the provider served nothing inside a {0}-minute window, so this run can prove nothing",
            LiveNewsProviderFactory.LookbackMinutes);

        rows.Select(row => Normalize(row.Url)).Should().OnlyHaveUniqueItems(
            "two passes over one window must not duplicate a story — the dedup key is the idempotence guard");

        foreach (string key in stableKeys)
        {
            beforeGroups[key].Any(stored.Contains).Should().BeTrue(
                "the provider served '{0}' in both probes, so the store must hold it under one of its forms", key);
        }

        stored.Except(mayBeStored).Should().BeEmpty(
            "the store holds a URL the provider never served in either probe — it is inventing stories");
    }

    // --- The lookback contract, asserted against the provider's own timestamps (gh#464 case 1) ---

    [LiveNewsProviderFact]
    public async Task StoredNews_ShouldHonourTheLookbackWindow()
    {
        // FAILURE MODE: the `publishedAt < since` filter inverted or ignored, so the poller stores exactly the
        // articles it was asked to exclude. Non-emptiness cannot see this — an inverted filter still returns a
        // populated list (14 items on the payload this was proven against), just the wrong one.
        DateTimeOffset since = DateTimeOffset.UtcNow.AddMinutes(-LiveNewsProviderFactory.LookbackMinutes);

        await IngestAsync(_factory);
        List<NewsRecord> rows = await ReadAllAsync(_factory);

        rows.Should().NotBeEmpty("a live poll over {0} minutes should carry news", LiveNewsProviderFactory.LookbackMinutes);
        rows.Should().OnlyContain(
            row => row.PublishedAt >= since,
            "every stored story must fall inside the {0}-minute window the poller asked for",
            LiveNewsProviderFactory.LookbackMinutes);

        // The mapped shape the rest of R-2 reads, asserted on real payloads rather than fabricated ones.
        rows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.Url), "a URL is the dedup identity");
        rows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.Title), "an untitled story is unreadable");
        rows.Should().OnlyContain(row => row.SourceFeeds.Contains("finnhub"), "provenance names the feed that carried it");
    }

    // NOT COVERED HERE, deliberately, and recorded rather than implied (QA contract — "the fixture must be able
    // to produce what the test guards against"):
    //
    //   * SAME-FEED over-collapse. The R-2 fuzzy fallback skips candidates already carrying the feed, so two
    //     near-identical headlines from ONE provider must stay two rows. Removing that skip in production changes
    //     NOTHING on live payloads — a real Finnhub pull carries no two headlines similar enough, within the
    //     60-minute gap, to trip `AreLikelyTheSameStory` (verified: the defect was injected and every assertion
    //     here stayed green). It needs constructed input, which is the stubbed gh#360 tier's job — where it is
    //     currently absent too; see the PR for that coverage gap.
    //   * CROSS-FEED collapse. Blocked by gh#1125, declared below rather than skipped silently.

    // --- A refused provider must not sink the other (gh#464 case 4) ---

    [LiveNewsProviderFact]
    public async Task ARefusedProvider_ShouldNotSinkTheOther()
    {
        // FAILURE MODE: one provider rejecting the request aborts the whole pass, so a single bad key or a 429
        // costs the desk ALL news rather than one feed's share. Proven against a real provider refusal, not a
        // stubbed throw — the gh#358 guard catches `Exception`, and only a live call shows what the client
        // actually throws from inside (an HttpRequestException out of EnsureSuccessStatusCode).
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
        + "on any environment, staging included. Deliberately a declared block rather than a passing test: a "
        + "suite that reports coverage it does not have is worse than no suite (PR #1014 / #1013).")]
    public Task SameStory_ShouldCollapseToOneRecord_AcrossLiveProviders() =>
        throw new NotSupportedException(
            "Unblock with gh#1125, then assert: a URL both feeds carry becomes exactly one NewsRecord whose "
            + "SourceFeeds contains finnhub and tiingo.");

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
        // PINS OBSERVED BEHAVIOUR, gh#1125: the configured Tiingo token is valid but its plan does not include
        // the News API, so every news call is refused. Asserting it keeps "R-2 has one live feed" visible in CI
        // instead of tribal knowledge — and this goes RED the moment the entitlement is bought, which is exactly
        // the prompt to promote the blocked cross-source case above.
        //
        // The two halves are asserted SEPARATELY and both are load-bearing. Tiingo answers 403 to an unissued
        // token AND to a valid token without the news entitlement, so a status-only assertion would keep passing
        // — still citing gh#1125 — if the operator's token were merely revoked, which is a different problem with
        // a different fix. The token probe is the control that rules that out.
        string apiToken = LiveProviderConfig.TiingoApiKey!;

        TiingoWireProbe.ProbeResult token = await TiingoWireProbe.ProbeTokenAsync(apiToken);
        token.Status.Should().Be(
            HttpStatusCode.OK,
            "the token must authenticate against an endpoint every plan carries — a failure here means the "
            + "credential is dead, which is NOT what gh#1125 records");

        TiingoWireProbe.ProbeResult news = await TiingoWireProbe.ProbeNewsAsync(apiToken);
        news.Status.Should().Be(
            HttpStatusCode.Forbidden,
            "gh#1125 pins that this plan refuses news; a 200 means the entitlement now exists and "
            + "SameStory_ShouldCollapseToOneRecord_AcrossLiveProviders should be unblocked");
        news.Body.Should().Contain(
            "permission",
            "the refusal must be the ENTITLEMENT one ('You do not have permission to access the News API'), not "
            + "'Invalid token.' — the token probe above already passed, so a token refusal here would mean the "
            + "two endpoints disagree about the credential");

        // And the adapter must surface that refusal rather than swallowing it into an empty result.
        AdapterSnapshot adapters = await ReadThroughAdaptersAsync(_factory);
        adapters.FailureFor("tiingo").Should().NotBeNull(
            "the adapter must propagate the provider's refusal, so the gh#358 per-source guard can see it");
    }

    // --- Free-tier data quality (the gh#383 "Open" item) ---

    [LiveNewsProviderFact]
    public async Task FreeTierFeeds_ShouldReportTheirDataQuality()
    {
        // Not a judgement on the providers' editorial quality — it asserts the SHAPE the pipeline depends on and
        // reports the rest as evidence, so free-tier usability stops being an open question answered by nobody.
        // Engineering "Data sources" flags Finnhub free-tier quality as unverified; this is the first look, and
        // it is where gh#1123 (lookback) and gh#1124 (no tickers) came from.
        IReadOnlyList<WireArticle> wire = await FetchAsync(LiveProviderConfig.FinnhubApiKey!);
        AdapterSnapshot adapters = await ReadThroughAdaptersAsync(_factory);

        _output.WriteLine(Describe(wire, adapters));

        wire.Should().NotBeEmpty("a free tier that serves nothing is not a usable source");
        wire.Should().OnlyContain(
            article => !string.IsNullOrWhiteSpace(article.Headline),
            "an untitled story is unreadable in the blotter");
        wire.Should().OnlyContain(
            article => article.PublishedAt < DateTimeOffset.UtcNow.AddHours(1),
            "a publish time in the future means the provider's epoch is being misread");
        wire.Should().OnlyContain(
            article => article.PublishedAt > DateTimeOffset.UtcNow.AddDays(-30),
            "a publish time far outside any sane lookback means the provider's epoch is being misread");

        // DEFECT gh#1124: the general category tags no tickers, so gh#359 relevance resolution has no input.
        // Pinned as observed, not blessed — it flips into that issue's regression guard when a tagged feed lands.
        adapters.ItemsFor("finnhub").Should().OnlyContain(
            item => item.Tickers.Count == 0,
            "gh#1124 pins that Finnhub's general category carries NO tickers; tickers appearing here means the "
            + "gap has closed and gh#1124 should be revisited");
    }

    // --- Helpers ---

    // The in-window articles, keyed by what the pipeline may legitimately treat as one story, each key carrying
    // every raw form the provider served it under. One of those forms must appear in the store.
    private static Dictionary<string, List<string>> GroupInWindow(
        IEnumerable<WireArticle> articles,
        DateTimeOffset since) =>
        articles
            .Where(article => article.PublishedAt >= since)
            .GroupBy(article => Normalize(article.Url))
            .ToDictionary(
                group => group.Key,
                group => group.Select(article => Normalize(article.Url)).Distinct().ToList());

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

    // What each registered source SERVED or how it FAILED. This is the adapters' view, used only where the
    // adapter itself is the subject (the ticker pin, the Tiingo refusal) — never as the oracle for what the
    // store should contain, which comes off the wire.
    private static async Task<AdapterSnapshot> ReadThroughAdaptersAsync(LiveNewsProviderFactory factory)
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

        return new AdapterSnapshot(served, refused);
    }

    private static string Describe(IReadOnlyList<WireArticle> wire, AdapterSnapshot adapters)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<string> lines =
        [
            $"FREE-TIER DATA QUALITY (gh#1122) — observed {now:u}",
            $"  finnhub wire: {wire.Count} articles, "
            + $"{wire.Select(article => Normalize(article.Url)).Distinct().Count()} distinct URLs",
        ];

        // The age profile gh#1123 came from: how much of a live payload each candidate lookback admits.
        foreach (int window in (int[])[LiveNewsProviderFactory.ProductionDefaultLookbackMinutes, 240, 1440, LiveNewsProviderFactory.LookbackMinutes])
        {
            int inside = wire.Count(article => article.PublishedAt > now.AddMinutes(-window));
            string note = window == LiveNewsProviderFactory.ProductionDefaultLookbackMinutes
                ? "  <-- the SHIPPED default (gh#1123)"
                : string.Empty;
            lines.Add($"    within {window,5} min: {inside,4}{note}");
        }

        foreach ((string feed, IReadOnlyList<NewsItem> items) in adapters.Served)
        {
            lines.Add(
                $"  {feed} adapter: {items.Count} items, {items.Count(item => item.Tickers.Count > 0)} with tickers");
        }

        foreach ((string feed, Exception error) in adapters.Refused)
        {
            lines.Add($"  {feed} adapter: REFUSED — {error.Message}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private sealed class AdapterSnapshot(
        Dictionary<string, IReadOnlyList<NewsItem>> served,
        Dictionary<string, Exception> refused)
    {
        public IReadOnlyDictionary<string, IReadOnlyList<NewsItem>> Served => served;

        public IReadOnlyDictionary<string, Exception> Refused => refused;

        public IReadOnlyList<NewsItem> ItemsFor(string feed) =>
            served.TryGetValue(feed, out IReadOnlyList<NewsItem>? items) ? items : [];

        public Exception? FailureFor(string feed) =>
            refused.TryGetValue(feed, out Exception? error) ? error : null;
    }
}
