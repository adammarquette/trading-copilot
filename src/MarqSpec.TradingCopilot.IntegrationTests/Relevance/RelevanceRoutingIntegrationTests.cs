using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Relevance;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Relevance;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Relevance;

/// <summary>
/// Independent QA for news-relevance routing (gh#361, D2·2) — the deterministic, pure mapping from a news item's
/// tickers + text to matched instruments and topics against the deployment's global relevance config (gh#359, R-2,
/// R-6, ADR-0014). Pre-merge tier: the real API pipeline over a throwaway Postgres container, venue-independent.
/// </summary>
/// <remarks>
/// <para>
/// Written from the gh#361 "Verifies" list and the gh#359 acceptance criteria — not from the coding agent's
/// resolver implementation. Config (ticker↔instrument maps, topics) is seeded either straight through
/// <see cref="TradingCopilotDbContext"/> (when the test only cares that a given config resolves correctly) or
/// through the real <c>/api/relevance</c> endpoints (when the test is proving the config-edit → re-resolution
/// loop gh#418 hardened). Either way, resolution is driven by calling <see cref="NewsRelevanceService.ResolveAsync"/>
/// directly rather than waiting on the background <c>NewsRelevanceHost</c> timer, which
/// <see cref="RelevanceRoutingTestPostgresFactory"/> removes for exactly that determinism reason.
/// </para>
/// <para>
/// Two guards (the <c>NewsTopics_*</c> tests) write straight through the context to prove the DB-level
/// <c>CK_NewsTopics_Scope_NotUnknown</c> check constraint and the <c>IX_NewsTopics_Name</c> unique index from the
/// applied <c>AddNewsRelevance</c> migration are live — invisible to any in-memory provider, and a second line of
/// defense below the endpoint validation they also duplicate.
/// </para>
/// </remarks>
public sealed class RelevanceRoutingIntegrationTests : IClassFixture<RelevanceRoutingTestPostgresFactory>
{
    private static DateTimeOffset Now => new(2026, 7, 29, 15, 0, 0, TimeSpan.Zero);

    private readonly RelevanceRoutingTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public RelevanceRoutingIntegrationTests(RelevanceRoutingTestPostgresFactory factory)
    {
        _factory = factory;
        // Each test owns the store: the container is shared across the class, so start every test from empty.
        ResetStoreAsync().GetAwaiter().GetResult();
    }

    // --- Ticker↔instrument mapping (gh#361: "resolves an item to the configured instrument context(s)") ---

    [Fact]
    public async Task ResolveAsync_ShouldAttachTheMappedInstrument_WhenATickerHasOneMapping()
    {
        await SeedMapAsync("SPY", "ES");
        await SeedNewsAsync(NewsSeed("spy-item", tickers: ["SPY"]));

        int resolved = await ResolveAsync();

        resolved.Should().Be(1);
        NewsRecord row = await ReadNewsAsync("spy-item");
        row.MatchedInstruments.Should().BeEquivalentTo(["ES"]);
        row.MatchedTopics.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ShouldAttachEveryMappedInstrument_WhenATickerMapsToSeveral()
    {
        // The entity doc's first composition claim: "a ticker can map to several instruments" (SPY tracks both
        // the full-size ES future and the micro MES future).
        await SeedMapAsync("SPY", "ES");
        await SeedMapAsync("SPY", "MES");
        await SeedNewsAsync(NewsSeed("spy-multi", tickers: ["SPY"]));

        await ResolveAsync();

        NewsRecord row = await ReadNewsAsync("spy-multi");
        row.MatchedInstruments.Should().BeEquivalentTo(["ES", "MES"]);
    }

    [Fact]
    public async Task ResolveAsync_ShouldUnionAndDedupeInstruments_AcrossMultipleTickersOnOneItem()
    {
        // The entity doc's other composition claim: "several tickers [map] to one" (SPY and SPX both track the
        // S&P and both resolve to ES) COMPOSED with a genuinely distinct ticker (QQQ -> NQ) on the same item. ES
        // must appear once, not twice, despite two tickers mapping to it.
        await SeedMapAsync("SPY", "ES");
        await SeedMapAsync("SPX", "ES");
        await SeedMapAsync("QQQ", "NQ");
        await SeedNewsAsync(NewsSeed("multi-ticker", tickers: ["SPY", "SPX", "QQQ"]));

        await ResolveAsync();

        NewsRecord row = await ReadNewsAsync("multi-ticker");
        row.MatchedInstruments.Should().BeEquivalentTo(["ES", "NQ"], "ES must be deduped despite two tickers mapping to it");
    }

    // --- Global / topic bucketing (gh#361: "macro items surface globally; topic tags applied correctly") ---

    [Fact]
    public async Task ResolveAsync_ShouldMatchAGlobalTopic_AndAttachNoInstrument()
    {
        await SeedTopicAsync("fomc", TopicScope.Global, instrument: null, "FOMC");
        await SeedNewsAsync(NewsSeed(
            "fomc-macro", title: "Fed holds rates", summary: "The FOMC left the target range unchanged.", tickers: []));

        await ResolveAsync();

        NewsRecord row = await ReadNewsAsync("fomc-macro");
        row.MatchedTopics.Should().BeEquivalentTo(["fomc"]);
        row.MatchedInstruments.Should().BeEmpty("a global topic surfaces the item without attaching it to any instrument");
    }

    [Fact]
    public async Task ResolveAsync_ShouldAttachAnInstrument_ViaAnInstrumentScopedTopicMatch_WithNoTickerMap()
    {
        // The SECOND path to instrument relevance beyond the ticker maps (NewsRelevanceResolver's documented
        // contract): an instrument-scoped topic match attaches its instrument even when the item carries no
        // ticker at all.
        await SeedTopicAsync("oil-inventory", TopicScope.Instrument, instrument: "CL", "EIA", "crude inventories");
        await SeedNewsAsync(NewsSeed(
            "oil-story", title: "Crude inventories drop", summary: "EIA reported a larger-than-expected draw.", tickers: []));

        await ResolveAsync();

        NewsRecord row = await ReadNewsAsync("oil-story");
        row.MatchedTopics.Should().BeEquivalentTo(["oil-inventory"]);
        row.MatchedInstruments.Should().BeEquivalentTo(["CL"], "an instrument-scoped topic is relevance, not just a tag");
    }

    [Fact]
    public async Task ResolveAsync_ShouldComposeTickerMapAndTopicMatches_OnTheSameItem()
    {
        // Both paths on one item must compose, neither clobbering the other.
        await SeedMapAsync("SPY", "ES");
        await SeedTopicAsync("fomc", TopicScope.Global, instrument: null, "FOMC");
        await SeedNewsAsync(NewsSeed(
            "spy-fomc", summary: "The FOMC left rates unchanged, pressuring SPY futures.", tickers: ["SPY"]));

        await ResolveAsync();

        NewsRecord row = await ReadNewsAsync("spy-fomc");
        row.MatchedInstruments.Should().BeEquivalentTo(["ES"]);
        row.MatchedTopics.Should().BeEquivalentTo(["fomc"]);
    }

    [Fact]
    public async Task ResolveAsync_ShouldMatchTopicKeywords_CaseInsensitively()
    {
        // NewsRelevanceResolver's documented contract: "matching is case-insensitive". Real headlines vary in
        // casing constantly (ALL CAPS wire headlines are common), so a case-sensitive regression would silently
        // stop matching real-world stories while every same-casing test in the suite kept passing.
        await SeedTopicAsync("earnings", TopicScope.Global, instrument: null, "Earnings Call");
        await SeedNewsAsync(NewsSeed("earnings-caps", summary: "Q2 EARNINGS CALL beat estimates.", tickers: []));

        await ResolveAsync();

        NewsRecord row = await ReadNewsAsync("earnings-caps");
        row.MatchedTopics.Should().BeEquivalentTo(["earnings"]);
    }

    // --- No cross-contamination (gh#361: "instrument A's items never appear in instrument B's context") ---

    [Fact]
    public async Task ResolveAsync_ShouldNotLeakOneItemsInstrument_IntoAnothersMatchedSet()
    {
        // THE isolation guard. Both rows resolve in the SAME pass (one batch, one SaveChanges) -- a query or
        // batching bug that cross-wires rows would only be visible with more than one row in flight together.
        // Asserted with BeEquivalentTo (exact set), not Contain: a Contain-only check would pass even if ES
        // leaked onto the QQQ story, which is exactly the defect this test exists to catch.
        await SeedMapAsync("SPY", "ES");
        await SeedMapAsync("QQQ", "NQ");
        await SeedNewsAsync(
            NewsSeed("spy-story", tickers: ["SPY"]),
            NewsSeed("qqq-story", tickers: ["QQQ"]));

        int resolved = await ResolveAsync();

        resolved.Should().Be(2);
        NewsRecord spyRow = await ReadNewsAsync("spy-story");
        NewsRecord qqqRow = await ReadNewsAsync("qqq-story");
        spyRow.MatchedInstruments.Should().BeEquivalentTo(["ES"], "the QQQ mapping must never leak onto the SPY story");
        qqqRow.MatchedInstruments.Should().BeEquivalentTo(["NQ"], "the SPY mapping must never leak onto the QQQ story");
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotAttachAnInstrumentScopedTopic_ToAnItemThatDidNotMatchItsKeywords()
    {
        // The same isolation property, for the topic-driven attachment path: an instrument-scoped topic's
        // instrument must not spill onto a sibling row in the same batch that never matched its keywords.
        await SeedTopicAsync("oil-inventory", TopicScope.Instrument, instrument: "CL", "crude inventories");
        await SeedNewsAsync(
            NewsSeed("oil-story", summary: "A larger-than-expected draw in crude inventories.", tickers: []),
            NewsSeed("unrelated-story", title: "Local team wins championship", summary: "No overlap here.", tickers: ["SPY"]));

        await ResolveAsync();

        NewsRecord oilRow = await ReadNewsAsync("oil-story");
        NewsRecord unrelatedRow = await ReadNewsAsync("unrelated-story");
        oilRow.MatchedInstruments.Should().BeEquivalentTo(["CL"]);
        unrelatedRow.MatchedInstruments.Should().BeEmpty("CL must never leak onto a story that did not match the oil topic's keywords");
        unrelatedRow.MatchedTopics.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ShouldLeaveAnUnrelatedItem_FullyUnmatched()
    {
        // gh#361's own wording: "an unrelated item stays unmatched" -- config exists (for OTHER things), but this
        // item's ticker is unmapped and its text matches no topic, so it must resolve to nothing on either axis.
        await SeedMapAsync("SPY", "ES");
        await SeedTopicAsync("fomc", TopicScope.Global, instrument: null, "FOMC");
        await SeedNewsAsync(NewsSeed(
            "unrelated", title: "Local team wins championship", summary: "Nothing here matches any configured topic.", tickers: ["ZZZZ"]));

        await ResolveAsync();

        NewsRecord row = await ReadNewsAsync("unrelated");
        row.MatchedInstruments.Should().BeEmpty();
        row.MatchedTopics.Should().BeEmpty();
    }

    // --- Unmapped tickers fall through cleanly (gh#361: "global/topic only, no misattribution, no crash") ---

    [Fact]
    public async Task ResolveAsync_ShouldFallThroughToTopicOnly_WhenItsTickerIsUnmapped()
    {
        await SeedTopicAsync("earnings", TopicScope.Global, instrument: null, "earnings");
        await SeedNewsAsync(NewsSeed("unmapped-ticker", summary: "Q2 earnings beat expectations.", tickers: ["ZZZZ"]));

        await ResolveAsync();

        NewsRecord row = await ReadNewsAsync("unmapped-ticker");
        row.MatchedInstruments.Should().BeEmpty("ZZZZ has no map and must contribute no instrument -- no misattribution");
        row.MatchedTopics.Should().BeEquivalentTo(["earnings"], "the item still falls through to its global/topic match");
    }

    [Fact]
    public async Task ResolveAsync_ShouldResolveTheWholeBatch_WithEmptyMatchesAndNoCrash_WhenNothingMapsOrMatches()
    {
        // "No crash, no partial write": an item with genuinely nothing to match must not throw, must not be
        // skipped/left pending, and must not poison the batch write for a sibling row that DOES match. Both rows
        // are seeded and resolved together in one pass to prove the empty-match row doesn't abort the shared
        // SaveChanges its sibling depends on.
        await SeedMapAsync("SPY", "ES");
        await SeedNewsAsync(
            NewsSeed("matches", tickers: ["SPY"]),
            NewsSeed("matches-nothing", title: "Local team wins championship", summary: "No configured map or topic applies.", tickers: ["ZZZZ"]));

        // No explicit try/catch: an unhandled exception here fails the test with that exception as the reported
        // reason, which is itself a valid (and legible) witness of "crashes" -- the same idiom the sibling
        // ingestion suite uses for its no-throw assertions.
        int resolved = await ResolveAsync();
        resolved.Should().Be(2, "both rows -- matched and unmatched -- must be resolved in the same pass");

        NewsRecord emptyRow = await ReadNewsAsync("matches-nothing");
        emptyRow.MatchedInstruments.Should().BeEmpty();
        emptyRow.MatchedTopics.Should().BeEmpty();
        emptyRow.RelevanceVersion.Should().NotBeNull("an empty-match row is still marked resolved, never left pending forever");
        emptyRow.RelevanceResolvedAt.Should().Be(Now);

        NewsRecord matchedRow = await ReadNewsAsync("matches");
        matchedRow.MatchedInstruments.Should().BeEquivalentTo(["ES"], "the sibling row's match must survive the same batch write");
    }

    // --- Deterministic + re-runnable, config round-trips (gh#359 AC; the gh#418 version mechanism) ---

    [Fact]
    public async Task ResolveAsync_ShouldBeIdempotent_OnASecondPassAgainstUnchangedConfig()
    {
        await SeedMapAsync("SPY", "ES");
        await SeedNewsAsync(NewsSeed("repeat-item", tickers: ["SPY"]));

        int firstPass = await ResolveAsync();
        int secondPass = await ResolveAsync();

        firstPass.Should().Be(1);
        secondPass.Should().Be(0, "nothing changed since the first pass -- there is nothing left to (re-)resolve");
        NewsRecord row = await ReadNewsAsync("repeat-item");
        row.MatchedInstruments.Should().BeEquivalentTo(["ES"], "re-resolving against unchanged config yields the identical matched set");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReResolvePredictably_WhenMapsAndTopicsAreAddedThroughTheConfigEndpoints()
    {
        // The full user-curated-config loop, end-to-end through the real /api/relevance surface (gh#359 AC:
        // "edits to the config persist and take effect on the next resolution" -- the exact mechanism gh#418
        // hardened from a wall-clock comparison to RelevanceConfigState.Version).
        await SeedNewsAsync(NewsSeed("evolves", summary: "The FOMC left rates unchanged.", tickers: ["SPY"]));

        int beforeConfig = await ResolveAsync();
        beforeConfig.Should().Be(1, "a never-resolved row always resolves on the first pass, even against empty config");
        NewsRecord before = await ReadNewsAsync("evolves");
        before.MatchedInstruments.Should().BeEmpty("SPY has no map yet");
        before.MatchedTopics.Should().BeEmpty("no topic exists yet");

        HttpClient client = await AuthenticatedClientAsync();
        using HttpResponseMessage mapResponse = await client.PostAsJsonAsync("/api/relevance/maps", new TickerMapRequest("SPY", "ES"));
        mapResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using HttpResponseMessage topicResponse = await client.PostAsJsonAsync(
            "/api/relevance/topics", new TopicRequest("fomc", ["FOMC"], TopicScope.Global, null));
        topicResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        int afterConfig = await ResolveAsync();

        afterConfig.Should().Be(1, "the config edit stamped a new version, so the one existing row is stale and re-resolves");
        NewsRecord after = await ReadNewsAsync("evolves");
        after.MatchedInstruments.Should().BeEquivalentTo(["ES"], "the map added through the endpoint now applies");
        after.MatchedTopics.Should().BeEquivalentTo(["fomc"], "the topic added through the endpoint now applies");
    }

    // --- DB-enforced guards the applied migrations bring live (gh#359/gh#361: refusable-zero + unique name) ---

    [Fact]
    public async Task NewsTopics_ShouldRefuseScopeUnknown_ByTheAppliedCheckConstraint()
    {
        // RelevanceEndpoints.ValidateTopic already refuses Scope=Unknown above the database. This proves the
        // SAME refusal holds when written straight through the context, bypassing that endpoint validation --
        // CK_NewsTopics_Scope_NotUnknown from the applied AddNewsRelevance migration, not merely the model builder.
        await ExecuteDbContextAsync(async database =>
        {
            database.NewsTopics.Add(new NewsTopic
            {
                Id = Guid.NewGuid(),
                Name = "bad-scope",
                Keywords = ["x"],
                Scope = (TopicScope)0, // Unknown -- never a real scope; refusable zero
                Instrument = null,
            });

            Func<Task> save = () => database.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>("the database, not just the endpoint, must refuse an unset scope")
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                    && (error.ConstraintName == "CK_NewsTopics_Scope_NotUnknown"
                        || error.MessageText.Contains("CK_NewsTopics_Scope_NotUnknown", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public async Task NewsTopics_ShouldRefuseADuplicateName_ByTheUniqueIndex()
    {
        // RelevanceEndpoints.CreateTopicAsync already refuses a duplicate name with 409 above the database. This
        // proves the SAME refusal holds when written straight through the context -- IX_NewsTopics_Name (unique)
        // from the applied migration, not merely the endpoint's own pre-check (which could race two concurrent
        // writers without the index behind it).
        await ExecuteDbContextAsync(async database =>
        {
            database.NewsTopics.Add(new NewsTopic { Id = Guid.NewGuid(), Name = "fomc", Keywords = ["FOMC"], Scope = TopicScope.Global });
            await database.SaveChangesAsync();
        });

        await ExecuteDbContextAsync(async database =>
        {
            database.NewsTopics.Add(new NewsTopic { Id = Guid.NewGuid(), Name = "fomc", Keywords = ["Federal Reserve"], Scope = TopicScope.Global });

            Func<Task> save = () => database.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>("two topics must never share a name")
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.UniqueViolation
                    && (error.ConstraintName == "IX_NewsTopics_Name"
                        || error.MessageText.Contains("IX_NewsTopics_Name", StringComparison.Ordinal)));
        });
    }

    // --- Helpers ---

    private static NewsRecord NewsSeed(
        string dedupKey,
        string title = "Market update",
        string summary = "See story for details.",
        IEnumerable<string>? tickers = null) =>
        new()
        {
            DedupKey = dedupKey,
            Type = "news",
            Url = $"https://example.com/{dedupKey}",
            Title = title,
            Summary = summary,
            PublishedAt = Now.AddMinutes(-5),
            Tickers = [.. tickers ?? []],
            SourceFeeds = ["finnhub"],
            RecordedAt = Now.AddMinutes(-5),
        };

    private async Task<int> ResolveAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        NewsRelevanceService service = scope.ServiceProvider.GetRequiredService<NewsRelevanceService>();
        return await service.ResolveAsync(Now, CancellationToken.None);
    }

    private Task SeedNewsAsync(params NewsRecord[] news) =>
        ExecuteDbContextAsync(async database =>
        {
            database.News.AddRange(news);
            await database.SaveChangesAsync();
        });

    private Task SeedMapAsync(string ticker, string instrument) =>
        ExecuteDbContextAsync(async database =>
        {
            database.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = ticker, Instrument = instrument });
            await database.SaveChangesAsync();
        });

    private Task SeedTopicAsync(string name, TopicScope topicScope, string? instrument, params string[] keywords) =>
        ExecuteDbContextAsync(async database =>
        {
            database.NewsTopics.Add(new NewsTopic
            {
                Id = Guid.NewGuid(),
                Name = name,
                Keywords = [.. keywords],
                Scope = topicScope,
                Instrument = instrument,
            });
            await database.SaveChangesAsync();
        });

    private Task<NewsRecord> ReadNewsAsync(string dedupKey) =>
        QueryDbContextAsync(database => database.News.AsNoTracking().SingleAsync(n => n.DedupKey == dedupKey));

    private Task ResetStoreAsync() =>
        ExecuteDbContextAsync(async database =>
        {
            // Each test owns the store: the container is shared across the class, so start every test from empty.
            await database.News.ExecuteDeleteAsync();
            await database.NewsTopics.ExecuteDeleteAsync();
            await database.TickerInstrumentMaps.ExecuteDeleteAsync();
            await database.RelevanceConfigStates.ExecuteDeleteAsync();
        });

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }

    private async Task<T> QueryDbContextAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }
}
