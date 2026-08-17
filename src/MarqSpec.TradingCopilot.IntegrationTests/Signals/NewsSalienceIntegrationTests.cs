using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Signals;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Signals;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Signals;

/// <summary>
/// Independent QA for gh#362 Part A — importance-starring salience (gh#27, ADR-0014): a star raises the salience
/// of <b>similar future</b> news, a mute is its down-weight inverse, un-starring reverts, and a <b>salience
/// floor</b> keeps a muted item visible (no filter bubble). Pre-merge tier: the real <c>/api/news</c> +
/// <c>/api/news/feedback</c> endpoints over a throwaway Postgres container, venue-independent.
/// </summary>
/// <remarks>
/// <para>
/// Written from the gh#362 "Verifies" list and the gh#27 acceptance criteria — not from the coding agent's
/// implementation. News is seeded directly through <see cref="TradingCopilotDbContext"/> already
/// relevance-resolved (<c>MatchedInstruments</c> / <c>MatchedTopics</c> set on the row) — gh#359 relevance mapping
/// itself is <see cref="MarqSpec.TradingCopilot.IntegrationTests.Relevance.RelevanceRoutingIntegrationTests"/>'s
/// concern, not this suite's; this suite drives only the per-user salience layer on top of it.
/// </para>
/// <para>
/// Part B — the safety invariant that a star/mute can never reach the risk gate — is
/// <see cref="SalienceIsNotARiskControlIntegrationTests"/>, a separate file because it drives a completely
/// different seam (the order-submit → gate path, with the adversarial venue stub) than the news feed read here.
/// </para>
/// <para>
/// Two guards (the <c>SoftSignalFeedbacks_*</c> tests) write straight through the context to prove the DB-level
/// <c>CK_SoftSignalFeedback_Kind_NotUnknown</c> check constraint and the per-axis <c>UX_SoftSignalFeedback_Importance</c>
/// filtered unique index from the applied <c>AddSoftSignalDirectionAxis</c> migration (gh#762) are live — invisible to any in-memory
/// provider, and a second line of defense below the endpoint validation they also duplicate (mirrors the
/// <c>NewsTopics_*</c> tests in <c>RelevanceRoutingIntegrationTests</c>).
/// </para>
/// </remarks>
public sealed class NewsSalienceIntegrationTests : IClassFixture<SalienceTestPostgresFactory>
{
    private readonly SalienceTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public NewsSalienceIntegrationTests(SalienceTestPostgresFactory factory)
    {
        _factory = factory;
        // Each test owns the store: the container is shared across the class, so start every test from empty.
        ResetStoreAsync().GetAwaiter().GetResult();
    }

    // --- Cold-start (gh#27 AC: "with no stars, salience == the R-2 base; stars only adjust from there") ---

    [Fact]
    public async Task GetFeed_ShouldScoreEveryItemAtBaseRelevance_WhenTheOperatorHasNoFeedback()
    {
        HttpClient client = await AuthenticatedClientAsync();
        await SeedNewsAsync(
            News("https://example.com/cold-matched", DateTimeOffset.UtcNow.AddHours(-1), ["MES"], ["fomc"], ["finnhub"]),
            News("https://example.com/cold-unmatched", DateTimeOffset.UtcNow.AddHours(-2), [], [], ["tiingo"]));

        NewsFeedResponse feed = await GetFeedAsync(client, limit: 10);

        feed.Items.Should().HaveCount(2, "cold-start hard-filters nothing -- every candidate in the window returns");
        feed.Items.Should().OnlyContain(item => item.Multiplier == 1.0, "no feedback exists, so every item scores at exactly base -- 1.0, not merely close to it");
        feed.Items.Single(item => item.DedupKey == "https://example.com/cold-matched").BaseRelevance.Should().Be(1.0, "a matched item outranks an unmatched one even at cold-start (ADR-0014)");
        feed.Items.Single(item => item.DedupKey == "https://example.com/cold-unmatched").BaseRelevance.Should().Be(0.5, "an unmatched item is never zero -- it still surfaces, just lower (no filter bubble)");
    }

    // --- Starring raises salience of SIMILAR FUTURE items; an unrelated item is unchanged (gh#362 Verifies #1) ---

    [Fact]
    public async Task GetFeed_ShouldRaiseSalienceOfSimilarItems_AndReorderThemAboveANewerUnrelatedItem_WhenAnItemWasStarred()
    {
        const string starredKey = "https://example.com/salience-starred";
        const string futureSimilarKey = "https://example.com/salience-future-similar";
        const string unrelatedKey = "https://example.com/salience-unrelated";

        HttpClient client = await AuthenticatedClientAsync();

        // The item the operator stars: shares instrument, topic AND source with itself (trivially) -- so its own
        // re-read is also boosted, which is the mechanism, not a bug (a per-dimension weight, not a per-item flag).
        await SeedNewsAsync(News(starredKey, DateTimeOffset.UtcNow.AddHours(-3), ["MES"], ["fomc"], ["finnhub"]));
        (await StarAsync(client, starredKey)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // A LATER item (published after the star) sharing only instrument + source -- the literal "similar future
        // item" gh#27 describes. Published more recently than the starred item, but NOT as recently as the
        // unrelated item below, so pure recency alone would NOT explain any reordering in its favour.
        await SeedNewsAsync(News(futureSimilarKey, DateTimeOffset.UtcNow.AddHours(-2), ["MES"], [], ["finnhub"]));

        // An UNRELATED item -- shares no dimension with the star -- published MOST recently, so pure
        // recency would rank it first absent any salience effect.
        await SeedNewsAsync(News(unrelatedKey, DateTimeOffset.UtcNow.AddHours(-1), ["CL"], ["opec"], ["tiingo"]));

        NewsFeedResponse feed = await GetFeedAsync(client, limit: 10);

        feed.Items.Select(item => item.DedupKey).Should().Equal(
            [starredKey, futureSimilarKey, unrelatedKey],
            "the starred item (3h old) and its similar successor (2h old) must outrank the unrelated item published "
            + "just 1h ago -- salience reordered the feed, recency alone would have put the unrelated item first");

        feed.Items.Single(item => item.DedupKey == starredKey).Multiplier.Should().BeApproximately(3.5, 0.01,
            "1 (base) + 1.0 instrument + 1.0 topic + 0.5 source, from its own star");
        feed.Items.Single(item => item.DedupKey == futureSimilarKey).Multiplier.Should().BeApproximately(2.5, 0.01,
            "1 (base) + 1.0 instrument + 0.5 source -- it shares no topic with the star");
        feed.Items.Single(item => item.DedupKey == unrelatedKey).Multiplier.Should().Be(1.0,
            "shares NO dimension with the star -- unchanged at base, exactly, not merely close to it");

        // ADR-0014's explainability requirement: weighting is never a hidden score.
        NewsFeedItemResponse boosted = feed.Items.Single(item => item.DedupKey == starredKey);
        boosted.WhyWeighted.Should().Contain("up because you starred");
        boosted.Reasons.Should().NotBeEmpty();
    }

    // --- Un-star reverts to base (gh#362 Verifies #2 / gh#27 AC "reversible... round-trip") ---

    [Fact]
    public async Task GetFeed_ShouldRevertToExactlyBaseRelevance_WhenFeedbackIsCleared()
    {
        const string starredKey = "https://example.com/salience-revert-starred";
        const string similarKey = "https://example.com/salience-revert-similar";

        HttpClient client = await AuthenticatedClientAsync();
        await SeedNewsAsync(News(starredKey, DateTimeOffset.UtcNow.AddHours(-2), ["MES"], [], ["finnhub"]));
        await SeedNewsAsync(News(similarKey, DateTimeOffset.UtcNow.AddHours(-1), ["MES"], [], ["finnhub"]));
        (await StarAsync(client, starredKey)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        NewsFeedResponse boosted = await GetFeedAsync(client, limit: 10);
        boosted.Items.Single(item => item.DedupKey == similarKey).Multiplier.Should().BeGreaterThan(1.0,
            "precondition: the star must actually have raised the similar item's salience");

        (await ClearAsync(client, starredKey)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        NewsFeedResponse reverted = await GetFeedAsync(client, limit: 10);
        reverted.Items.Single(item => item.DedupKey == similarKey).Multiplier.Should().Be(1.0,
            "un-starring removes the ONLY feedback -- the profile is empty again, so salience round-trips to exactly base");
    }

    // --- Mute is the down-weight inverse (gh#362 Verifies #2 / ADR-0014) ---

    [Fact]
    public async Task GetFeed_ShouldLowerSalience_WhenASimilarItemWasMuted()
    {
        const string mutedKey = "https://example.com/salience-muted-source-only";
        const string similarKey = "https://example.com/salience-muted-similar";

        HttpClient client = await AuthenticatedClientAsync();
        // Muted along ONE weaker dimension (source, weight 0.5) so the result is lowered but deliberately NOT yet
        // at the floor -- a distinct claim from the floor/no-suppression guard below.
        await SeedNewsAsync(News(mutedKey, DateTimeOffset.UtcNow.AddHours(-2), [], [], ["finnhub"]));
        await SeedNewsAsync(News(similarKey, DateTimeOffset.UtcNow.AddHours(-1), ["MES"], [], ["finnhub"]));
        (await MuteAsync(client, mutedKey)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        NewsFeedResponse feed = await GetFeedAsync(client, limit: 10);

        NewsFeedItemResponse scored = feed.Items.Single(item => item.DedupKey == similarKey);
        scored.Multiplier.Should().BeApproximately(0.5, 0.01, "1 (base) - 0.5 source mute");
        scored.Multiplier.Should().BeLessThan(1.0).And.BeGreaterThan(0.25, "down-weighted, but nowhere near the floor yet");
        scored.WhyWeighted.Should().Contain("down because you muted");
    }

    // --- The salience floor: never total suppression, no filter bubble (gh#362 Verifies #2 / ADR-0014) ---

    [Fact]
    public async Task GetFeed_ShouldClampAtTheFloorAndNeverSuppressTheItem_WhenHeavilyMuted()
    {
        const string mutedKey = "https://example.com/salience-heavily-muted";
        const string similarKey = "https://example.com/salience-heavily-muted-similar";

        HttpClient client = await AuthenticatedClientAsync();
        // One item muted across ALL THREE dimensions at once: -1.0 (instrument) -1.0 (topic) -0.5 (source) against
        // a base of 1.0 -- a raw score of -1.5, deep past zero. If the floor did not clamp, this item would be
        // driven to a negative (or zero) salience, which IS a hard suppression by another name.
        await SeedNewsAsync(News(mutedKey, DateTimeOffset.UtcNow.AddHours(-2), ["MES"], ["fomc"], ["finnhub"]));
        await SeedNewsAsync(News(similarKey, DateTimeOffset.UtcNow.AddHours(-1), ["MES"], ["fomc"], ["finnhub"]));
        (await MuteAsync(client, mutedKey)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        NewsFeedResponse feed = await GetFeedAsync(client, limit: 10);

        feed.Items.Should().Contain(item => item.DedupKey == similarKey,
            "a heavily-muted-by-similarity item is STILL returned -- starring/muting reorders, it never hard-filters");
        NewsFeedItemResponse scored = feed.Items.Single(item => item.DedupKey == similarKey);
        scored.Multiplier.Should().Be(0.25, "raw would be -1.5 -- the floor clamps it to exactly the configured floor, never below");
        scored.Salience.Should().BeGreaterThan(0.0, "salience must never reach zero or negative -- that would BE a filter bubble");
    }

    // --- SoftSignalFeedback persistence drives the weighting (gh#362 Verifies #3 / gh#27 AC) ---

    [Fact]
    public async Task SetFeedback_ShouldUpsertReplacingTheKind_RatherThanStackingASecondRow()
    {
        const string key = "https://example.com/salience-upsert";
        HttpClient client = await AuthenticatedClientAsync();
        await SeedNewsAsync(News(key, DateTimeOffset.UtcNow, ["MES"], [], ["finnhub"]));

        (await StarAsync(client, key)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        NewsFeedResponse afterStar = await GetFeedAsync(client, limit: 5);
        afterStar.Items.Should().ContainSingle().Which.Feedback.Should().Be(SoftSignalKind.Star);

        // Re-rating the SAME item replaces the kind -- an upsert, never a stacked second row.
        (await MuteAsync(client, key)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        NewsFeedResponse afterMute = await GetFeedAsync(client, limit: 5);
        afterMute.Items.Should().ContainSingle().Which.Feedback.Should().Be(SoftSignalKind.Mute);

        await ExecuteDbContextAsync(async database =>
        {
            List<SoftSignalFeedback> rows = await database.SoftSignalFeedbacks.IgnoreQueryFilters()
                .Where(row => row.NewsDedupKey == key)
                .ToListAsync();
            rows.Should().ContainSingle("re-rating replaces the kind rather than stacking a second row")
                .Which.Kind.Should().Be(SoftSignalKind.Mute);
        });
    }

    [Fact]
    public async Task ClearFeedback_ShouldDeleteTheRow_AndReturn404NotFound_WhenNoneExists()
    {
        const string key = "https://example.com/salience-clear-roundtrip";
        HttpClient client = await AuthenticatedClientAsync();
        await SeedNewsAsync(News(key, DateTimeOffset.UtcNow, ["MES"], [], ["finnhub"]));
        (await StarAsync(client, key)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ClearAsync(client, key)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await ExecuteDbContextAsync(async database =>
            (await database.SoftSignalFeedbacks.IgnoreQueryFilters().AnyAsync(row => row.NewsDedupKey == key))
                .Should().BeFalse("un-starring deletes the row -- it does not merely zero it out"));

        (await ClearAsync(client, key)).StatusCode.Should().Be(HttpStatusCode.NotFound, "nothing is left to clear");
    }

    [Fact]
    public async Task SetFeedback_ShouldReturn400BadRequest_AndPersistNothing_WhenKindIsUnknown()
    {
        const string key = "https://example.com/salience-unknown-kind";
        HttpClient client = await AuthenticatedClientAsync();
        await SeedNewsAsync(News(key, DateTimeOffset.UtcNow, ["MES"], [], ["finnhub"]));

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/news/feedback", new NewsFeedbackRequest(key, SoftSignalKind.Unknown));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Unknown is a refusable zero -- never a real kind");
        await ExecuteDbContextAsync(async database =>
            (await database.SoftSignalFeedbacks.IgnoreQueryFilters().AnyAsync(row => row.NewsDedupKey == key))
                .Should().BeFalse());
    }

    [Fact]
    public async Task SetFeedback_ShouldReturn404NotFound_WhenTheRatedItemDoesNotExist()
    {
        HttpClient client = await AuthenticatedClientAsync();

        using HttpResponseMessage response = await StarAsync(client, "https://example.com/salience-phantom-key");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "no feedback on a phantom key");
    }

    // --- DB-level guards (second line of defense below the endpoint's own validation) ---

    [Fact]
    public async Task SoftSignalFeedbacks_ShouldRefuseKindUnknown_ByTheAppliedCheckConstraint()
    {
        // NewsEndpoints.SetFeedbackAsync already refuses Kind = Unknown above the database (the test above). This
        // proves the SAME refusal holds when written straight through the context, bypassing that endpoint
        // validation -- CK_SoftSignalFeedback_Kind_NotUnknown from the applied AddSoftSignalFeedback migration.
        await ExecuteDbContextAsync(async database =>
        {
            database.SoftSignalFeedbacks.Add(new SoftSignalFeedback
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                NewsDedupKey = "https://example.com/salience-db-check-constraint",
                Kind = (SoftSignalKind)0, // Unknown -- never a real kind; refusable zero
                CreatedAt = DateTimeOffset.UtcNow,
            });

            Func<Task> save = () => database.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>("the database, not just the endpoint, must refuse an unset kind")
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                    && (error.ConstraintName == "CK_SoftSignalFeedback_Kind_NotUnknown"
                        || error.MessageText.Contains("CK_SoftSignalFeedback_Kind_NotUnknown", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public async Task SoftSignalFeedbacks_ShouldRefuseADuplicateImportanceRating_ByTheAxisUniqueIndex()
    {
        // NewsEndpoints.SetFeedbackAsync already upserts within an axis on (UserId, NewsDedupKey) above the database
        // (the upsert test above). This proves the SAME one-importance-rating-per-operator-per-item invariant holds
        // when written straight through the context -- UX_SoftSignalFeedback_Importance (the ADR-0014 gh#762 filtered
        // unique index over the Star/Mute kinds), not merely the endpoint's own read-then-write, which could race two
        // concurrent writers without the index behind it (the DbUpdateException race SetFeedbackAsync recovers from).
        // A DIRECTION rating (👍/👎) on the same item is a DIFFERENT axis and is NOT refused -- the two-axis
        // independence and the direction index are QA #971's independent coverage.
        Guid userId = Guid.NewGuid();
        const string key = "https://example.com/salience-db-unique-index";

        await ExecuteDbContextAsync(async database =>
        {
            database.SoftSignalFeedbacks.Add(new SoftSignalFeedback
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                NewsDedupKey = key,
                Kind = SoftSignalKind.Star,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await database.SaveChangesAsync();
        });

        await ExecuteDbContextAsync(async database =>
        {
            database.SoftSignalFeedbacks.Add(new SoftSignalFeedback
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                NewsDedupKey = key,
                Kind = SoftSignalKind.Mute,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            Func<Task> save = () => database.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>("one operator must never carry two importance (star/mute) rows for the same item")
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.UniqueViolation
                    && (error.ConstraintName == "UX_SoftSignalFeedback_Importance"
                        || error.MessageText.Contains("UX_SoftSignalFeedback_Importance", StringComparison.Ordinal)));
        });
    }

    // --- Helpers ---

    private static NewsRecord News(
        string dedupKey, DateTimeOffset publishedAt, string[] instruments, string[] topics, string[] sources) =>
        new()
        {
            DedupKey = dedupKey,
            Type = "news",
            Url = dedupKey,
            Title = $"Seed for {dedupKey}",
            Summary = "A seeded news item for the gh#362 salience suite.",
            PublishedAt = publishedAt,
            Tickers = [.. instruments],
            SourceFeeds = [.. sources],
            MatchedInstruments = [.. instruments],
            MatchedTopics = [.. topics],
            RecordedAt = publishedAt,
        };

    private Task SeedNewsAsync(params NewsRecord[] news) =>
        ExecuteDbContextAsync(async database =>
        {
            database.News.AddRange(news);
            await database.SaveChangesAsync();
        });

    private static Task<HttpResponseMessage> StarAsync(HttpClient client, string dedupKey) =>
        client.PutAsJsonAsync("/api/news/feedback", new NewsFeedbackRequest(dedupKey, SoftSignalKind.Star));

    private static Task<HttpResponseMessage> MuteAsync(HttpClient client, string dedupKey) =>
        client.PutAsJsonAsync("/api/news/feedback", new NewsFeedbackRequest(dedupKey, SoftSignalKind.Mute));

    private static Task<HttpResponseMessage> ClearAsync(HttpClient client, string dedupKey) =>
        client.DeleteAsync($"/api/news/feedback?dedupKey={Uri.EscapeDataString(dedupKey)}");

    private async Task<NewsFeedResponse> GetFeedAsync(HttpClient client, int limit)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/news?limit={limit}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        NewsFeedResponse? feed = await response.Content.ReadFromJsonAsync<NewsFeedResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(feed);
        return feed;
    }

    private Task ResetStoreAsync() =>
        ExecuteDbContextAsync(async database =>
        {
            // Each test owns the store: the container is shared across the class, so start every test from empty.
            await database.SoftSignalFeedbacks.IgnoreQueryFilters().ExecuteDeleteAsync();
            await database.News.ExecuteDeleteAsync();
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
}
