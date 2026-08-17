using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Signals;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Signals;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Signals;

/// <summary>
/// The <c>/api/news</c> handlers (gh#27, ADR-0014): star/mute feedback and the personalized feed. The properties
/// that matter: feedback is a validated <b>upsert</b> (one per operator per item) that round-trips on clear; a star
/// reorders the feed toward similar items while an unstarred item <b>stays visible</b> (no filter bubble); the
/// caller's own feedback + a "why weighted" reason are surfaced; and every read/write is R-20-scoped (one operator's
/// stars never touch another's feed).
/// </summary>
public class NewsEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private static readonly IOptions<SalienceOptions> _options = Options.Create(new SalienceOptions());
    private static readonly DateTimeOffset _t = new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static NewsFeedResponse FeedOf(IResult result) => (NewsFeedResponse)((IValueHttpResult)result).Value!;

    // A real SemanticSalienceAxis over fakes (it is sealed, so it cannot be faked directly). Default: an unavailable
    // provider -> an empty axis, i.e. the pre-#853 categorical-only feed. `available: true` with a `vectors` store
    // scores candidates against the stars via the pure helper (the seam is faked; the real pgvector read is QA-tier).
    private static SemanticSalienceAxis Axis(
        bool available = false, IReadOnlyDictionary<string, IReadOnlyList<float>>? vectors = null)
    {
        IReadOnlyDictionary<string, IReadOnlyList<float>> store =
            vectors ?? new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);
        IEmbeddingProvider provider = A.Fake<IEmbeddingProvider>();
        A.CallTo(() => provider.IsAvailable).Returns(available);
        INewsEmbeddingSimilarity similarity = A.Fake<INewsEmbeddingSimilarity>();
        A.CallTo(() => similarity.GetVectorsAsync(A<IReadOnlyCollection<string>>._, A<CancellationToken>._))
            .ReturnsLazily((IReadOnlyCollection<string> ids, CancellationToken _) =>
            {
                IReadOnlyList<StoredEmbedding> hits =
                    [.. ids.Where(store.ContainsKey).Select(id => new StoredEmbedding(id, store[id]))];
                return hits;
            });
        return new SemanticSalienceAxis(provider, similarity, NullLogger<SemanticSalienceAxis>.Instance);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<float>> Vectors(
        params (string OwnerId, float[] Vector)[] entries) =>
        entries.ToDictionary(entry => entry.OwnerId, entry => (IReadOnlyList<float>)entry.Vector, StringComparer.Ordinal);

    private async Task SeedNewsAsync(
        string dedupKey,
        DateTimeOffset publishedAt,
        IReadOnlyList<string>? instruments = null,
        IReadOnlyList<string>? topics = null,
        IReadOnlyList<string>? sources = null)
    {
        await using TradingCopilotDbContext context = Context();
        context.News.Add(new NewsRecord
        {
            DedupKey = dedupKey,
            Type = "news",
            Url = dedupKey,
            Title = dedupKey,
            Summary = string.Empty,
            PublishedAt = publishedAt,
            Tickers = [],
            SourceFeeds = [.. sources ?? []],
            RecordedAt = publishedAt,
            MatchedInstruments = [.. instruments ?? []],
            MatchedTopics = [.. topics ?? []],
        });
        await context.SaveChangesAsync();
    }

    private async Task<IResult> RateAsync(string dedupKey, SoftSignalKind kind = SoftSignalKind.Star, Guid? asUser = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await NewsEndpoints.SetFeedbackAsync(
            new NewsFeedbackRequest(dedupKey, kind), new FixedUser(asUser ?? _operator), context, default);
    }

    private async Task<IResult> ClearAsync(string dedupKey, SoftSignalAxis? axis = null, Guid? asUser = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await NewsEndpoints.ClearFeedbackAsync(dedupKey, axis, context, default);
    }

    // ---- feedback write ----

    [Fact]
    public async Task Star_CreatesFeedbackRow_OwnedByTheCaller()
    {
        await SeedNewsAsync("item", _t, instruments: ["ES"]);

        IResult result = await RateAsync("item");

        StatusOf(result).Should().Be(StatusCodes.Status204NoContent);
        await using TradingCopilotDbContext verify = Context();
        SoftSignalFeedback row = await verify.SoftSignalFeedbacks.SingleAsync();
        row.UserId.Should().Be(_operator);
        row.Kind.Should().Be(SoftSignalKind.Star);
        row.NewsDedupKey.Should().Be("item");
    }

    [Fact]
    public async Task ReRating_Replaces_RatherThanStacks()
    {
        await SeedNewsAsync("item", _t);
        await RateAsync("item", SoftSignalKind.Star);

        await RateAsync("item", SoftSignalKind.Mute);

        await using TradingCopilotDbContext verify = Context();
        SoftSignalFeedback row = await verify.SoftSignalFeedbacks.SingleAsync(); // still exactly one
        row.Kind.Should().Be(SoftSignalKind.Mute);
    }

    [Fact]
    public async Task Rating_APhantomKey_Is404()
    {
        IResult result = await RateAsync("does-not-exist");
        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData(SoftSignalKind.Unknown)]
    [InlineData((SoftSignalKind)99)]
    public async Task Rating_WithABadKind_Is400(SoftSignalKind kind)
    {
        await SeedNewsAsync("item", _t);
        IResult result = await RateAsync("item", kind);
        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Rating_WithNoKey_Is400()
    {
        await using TradingCopilotDbContext context = Context();
        IResult result = await NewsEndpoints.SetFeedbackAsync(
            new NewsFeedbackRequest("   ", SoftSignalKind.Star), new FixedUser(_operator), context, default);
        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Clear_RemovesTheFeedback_AndRoundTripsToBase()
    {
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item");

        await using (TradingCopilotDbContext context = Context())
        {
            IResult cleared = await NewsEndpoints.ClearFeedbackAsync("item", null, context, default);
            StatusOf(cleared).Should().Be(StatusCodes.Status204NoContent);
        }

        await using TradingCopilotDbContext verify = Context();
        (await verify.SoftSignalFeedbacks.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Clear_WhenAbsent_Is404()
    {
        await using TradingCopilotDbContext context = Context();
        IResult result = await NewsEndpoints.ClearFeedbackAsync("nope", null, context, default);
        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // ---- direction axis (gh#762): independent of importance ----

    [Fact]
    public async Task RateDirection_CreatesADirectionRow_OwnedByTheCaller()
    {
        await SeedNewsAsync("item", _t, instruments: ["ES"]);

        IResult result = await RateAsync("item", SoftSignalKind.ThumbsUp);

        StatusOf(result).Should().Be(StatusCodes.Status204NoContent);
        await using TradingCopilotDbContext verify = Context();
        SoftSignalFeedback row = await verify.SoftSignalFeedbacks.SingleAsync();
        row.UserId.Should().Be(_operator);
        row.Kind.Should().Be(SoftSignalKind.ThumbsUp);
    }

    [Fact]
    public async Task ReRatingDirection_Replaces_RatherThanStacks()
    {
        await SeedNewsAsync("item", _t);
        await RateAsync("item", SoftSignalKind.ThumbsUp);

        await RateAsync("item", SoftSignalKind.ThumbsDown);

        await using TradingCopilotDbContext verify = Context();
        SoftSignalFeedback row = await verify.SoftSignalFeedbacks.SingleAsync(); // still exactly one direction row
        row.Kind.Should().Be(SoftSignalKind.ThumbsDown);
    }

    [Fact]
    public async Task SettingDirection_LeavesTheImportanceRow_Untouched()
    {
        // Cross-axis independence: an operator may hold BOTH an importance and a direction row on one item. Setting
        // the direction must not disturb the star -- two rows survive, one per axis.
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item", SoftSignalKind.Star);

        await RateAsync("item", SoftSignalKind.ThumbsDown);

        await using TradingCopilotDbContext verify = Context();
        List<SoftSignalFeedback> rows = await verify.SoftSignalFeedbacks.ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.Kind).Should().BeEquivalentTo([SoftSignalKind.Star, SoftSignalKind.ThumbsDown]);
    }

    [Fact]
    public async Task SettingImportance_LeavesTheDirectionRow_Untouched()
    {
        // ...and the reverse: a star lands alongside an existing 👎 without replacing it.
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item", SoftSignalKind.ThumbsDown);

        await RateAsync("item", SoftSignalKind.Star);

        await using TradingCopilotDbContext verify = Context();
        List<SoftSignalFeedback> rows = await verify.SoftSignalFeedbacks.ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.Kind).Should().BeEquivalentTo([SoftSignalKind.ThumbsDown, SoftSignalKind.Star]);
    }

    [Fact]
    public async Task ReRatingImportance_LeavesTheDirectionRow_Untouched()
    {
        // Replacing WITHIN the importance axis (star -> mute) must not touch the direction row -- the replace is
        // scoped to the request's axis, never the whole item.
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item", SoftSignalKind.Star);
        await RateAsync("item", SoftSignalKind.ThumbsUp);

        await RateAsync("item", SoftSignalKind.Mute); // re-rate importance

        await using TradingCopilotDbContext verify = Context();
        List<SoftSignalFeedback> rows = await verify.SoftSignalFeedbacks.ToListAsync();
        rows.Select(r => r.Kind).Should().BeEquivalentTo([SoftSignalKind.Mute, SoftSignalKind.ThumbsUp]);
    }

    [Fact]
    public async Task ClearingDirection_DeletesOnlyTheDirectionRow()
    {
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item", SoftSignalKind.Star);
        await RateAsync("item", SoftSignalKind.ThumbsUp);

        IResult cleared = await ClearAsync("item", SoftSignalAxis.Direction);

        StatusOf(cleared).Should().Be(StatusCodes.Status204NoContent);
        await using TradingCopilotDbContext verify = Context();
        SoftSignalFeedback row = await verify.SoftSignalFeedbacks.SingleAsync(); // the star survives
        row.Kind.Should().Be(SoftSignalKind.Star);
    }

    [Fact]
    public async Task ClearingImportance_DeletesOnlyTheImportanceRow()
    {
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item", SoftSignalKind.Star);
        await RateAsync("item", SoftSignalKind.ThumbsUp);

        IResult cleared = await ClearAsync("item", SoftSignalAxis.Importance);

        StatusOf(cleared).Should().Be(StatusCodes.Status204NoContent);
        await using TradingCopilotDbContext verify = Context();
        SoftSignalFeedback row = await verify.SoftSignalFeedbacks.SingleAsync(); // the 👍 survives
        row.Kind.Should().Be(SoftSignalKind.ThumbsUp);
    }

    [Fact]
    public async Task Clear_WithoutAnAxis_DefaultsToImportance()
    {
        // Backward compatibility (the pre-gh#762 DELETE contract): no axis clears the star/mute, leaving direction.
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item", SoftSignalKind.Mute);
        await RateAsync("item", SoftSignalKind.ThumbsUp);

        IResult cleared = await ClearAsync("item"); // no axis

        StatusOf(cleared).Should().Be(StatusCodes.Status204NoContent);
        await using TradingCopilotDbContext verify = Context();
        SoftSignalFeedback row = await verify.SoftSignalFeedbacks.SingleAsync();
        row.Kind.Should().Be(SoftSignalKind.ThumbsUp);
    }

    [Fact]
    public async Task ClearingDirection_WhenOnlyImportanceExists_Is404()
    {
        // Clearing an axis with no row on it is a clean 404 -- the other axis's row is not collateral.
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item", SoftSignalKind.Star);

        IResult cleared = await ClearAsync("item", SoftSignalAxis.Direction);

        StatusOf(cleared).Should().Be(StatusCodes.Status404NotFound);
        await using TradingCopilotDbContext verify = Context();
        (await verify.SoftSignalFeedbacks.SingleAsync()).Kind.Should().Be(SoftSignalKind.Star); // untouched
    }

    [Fact]
    public async Task Clear_WithAnUnknownAxis_Is400()
    {
        await SeedNewsAsync("item", _t);
        await RateAsync("item", SoftSignalKind.Star);

        IResult result = await ClearAsync("item", SoftSignalAxis.Unknown);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Feed_SurfacesTheDirectionRating_WithAnExplanation()
    {
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item", SoftSignalKind.ThumbsUp);

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, Axis(), default));

        NewsFeedItemResponse rated = feed.Items.Single(item => item.DedupKey == "item");
        rated.Direction.Should().Be(SoftSignalKind.ThumbsUp);
        rated.DirectionReason.Should().NotBeNullOrWhiteSpace();
        rated.DirectionReason.Should().Contain("bullish");
    }

    [Fact]
    public async Task Feed_ADirectionRating_DoesNotReweightSurfacing()
    {
        // gh#762: direction is salience-INERT end to end. A 👍/👎 leaves the item's multiplier at base -- it never
        // reorders the feed the way a star does -- while still being surfaced on the read.
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item", SoftSignalKind.ThumbsUp);

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, Axis(), default));

        NewsFeedItemResponse rated = feed.Items.Single(item => item.DedupKey == "item");
        rated.Multiplier.Should().Be(1.0); // direction never boosts salience
        rated.Direction.Should().Be(SoftSignalKind.ThumbsUp);
    }

    [Fact]
    public async Task Feed_SurfacesBothAxes_WhenAnItemIsStarredAndRated()
    {
        // An item carrying BOTH a star and a 👍 surfaces both independently (and the read does not choke on the two
        // rows-per-item the two axes allow -- a regression guard on the per-item feedback grouping).
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item", SoftSignalKind.Star);
        await RateAsync("item", SoftSignalKind.ThumbsUp);

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, Axis(), default));

        NewsFeedItemResponse rated = feed.Items.Single(item => item.DedupKey == "item");
        rated.Feedback.Should().Be(SoftSignalKind.Star);      // importance
        rated.Direction.Should().Be(SoftSignalKind.ThumbsUp); // direction
    }

    // ---- feed read ----

    [Fact]
    public async Task Feed_ColdStart_OrdersByRecency()
    {
        await SeedNewsAsync("older", _t, instruments: ["ES"]);
        await SeedNewsAsync("newer", _t.AddMinutes(5), instruments: ["NQ"]);

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, Axis(), default));

        feed.Items.Select(item => item.DedupKey).Should().Equal("newer", "older"); // equal base -> recency
    }

    [Fact]
    public async Task Feed_RanksAStarredSimilarItem_AboveAMoreRecentUnrelatedOne()
    {
        await SeedNewsAsync("es-fresh", _t, instruments: ["ES"]);
        await SeedNewsAsync("nq-newer", _t.AddMinutes(5), instruments: ["NQ"]); // more recent
        await SeedNewsAsync("es-seed", _t.AddDays(-1), instruments: ["ES"]);
        await RateAsync("es-seed"); // star a prior ES story

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, Axis(), default));

        feed.Items.First().DedupKey.Should().Be("es-fresh"); // starred-similar beats mere recency
        feed.Items.Should().Contain(item => item.DedupKey == "nq-newer"); // unrelated item still visible (no bubble)
    }

    [Fact]
    public async Task Feed_SurfacesTheCallersOwnFeedback()
    {
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item");

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, Axis(), default));

        feed.Items.Single(item => item.DedupKey == "item").Feedback.Should().Be(SoftSignalKind.Star);
    }

    [Fact]
    public async Task Feed_ExplainsWhyAnItemWasWeighted()
    {
        await SeedNewsAsync("es-seed", _t.AddDays(-1), instruments: ["ES"]);
        await SeedNewsAsync("es-fresh", _t, instruments: ["ES"]);
        await RateAsync("es-seed");

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, Axis(), default));

        NewsFeedItemResponse boosted = feed.Items.Single(item => item.DedupKey == "es-fresh");
        boosted.Multiplier.Should().BeGreaterThan(1.0);
        boosted.WhyWeighted.Should().Contain("ES");
        boosted.Reasons.Should().Contain(reason => reason.Value == "ES");
    }

    [Fact]
    public async Task Feed_RespectsTheLimit()
    {
        for (int i = 0; i < 5; i++)
        {
            await SeedNewsAsync($"item-{i}", _t.AddMinutes(i));
        }

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(2, read, _options, Axis(), default));

        feed.Items.Should().HaveCount(2);
    }

    // ---- semantic-embedding axis (gh#853) ----

    [Fact]
    public async Task Feed_RanksASemanticallySimilarItem_WithNoSharedCategoricalDimension()
    {
        // The operator stars an ES story; the target shares NO instrument/topic/source with it, so categorically it is
        // unrelated. Its embedding is near the star's -> its multiplier rises on the semantic axis alone, with a
        // SemanticEmbedding reason, and it outranks a categorically-identical but semantically-unrelated peer.
        await SeedNewsAsync("star-seed", _t.AddDays(-1), instruments: ["ES"]);
        await SeedNewsAsync("sem-target", _t, instruments: ["NQ"]);
        await SeedNewsAsync("plain", _t.AddMinutes(1), instruments: ["NQ"]);
        await RateAsync("star-seed");

        SemanticSalienceAxis axis = Axis(available: true, vectors: Vectors(
            ("star-seed", new[] { 1f, 0f }),
            ("sem-target", new[] { 1f, 0f }),  // same direction as the star -> near
            ("plain", new[] { 0f, 1f })));     // orthogonal -> far

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, axis, default));

        NewsFeedItemResponse boosted = feed.Items.Single(item => item.DedupKey == "sem-target");
        boosted.Multiplier.Should().BeGreaterThan(1.0);
        boosted.Reasons.Should().Contain(reason => reason.Dimension == nameof(SalienceDimension.SemanticEmbedding));

        NewsFeedItemResponse plain = feed.Items.Single(item => item.DedupKey == "plain");
        boosted.Salience.Should().BeGreaterThan(plain.Salience); // semantic nearness beats the unrelated (newer) peer
        plain.Reasons.Should().NotContain(reason => reason.Dimension == nameof(SalienceDimension.SemanticEmbedding));
    }

    [Fact]
    public async Task Feed_BoostsARecentItemNearAStar_EvenWhenOlderNearItemsAreOutsideTheWindow()
    {
        // SF1 regression: the axis scores THIS window's candidates directly. A recent near-a-star item that IS in the
        // (small) window is boosted even though five equally-near items exist OUTSIDE it -- the exact case the old
        // design got wrong, where a global nearest-N over all rows was dominated by the out-of-window near items and
        // the recent, in-window one, intersected against that global set, got no boost.
        await SeedNewsAsync("star-seed", _t.AddDays(-3), instruments: ["ES"]);
        for (int i = 0; i < 5; i++)
        {
            await SeedNewsAsync($"old-near-{i}", _t.AddDays(-2).AddMinutes(i), instruments: ["NQ"]); // older -> outside a 2-item window
        }
        await SeedNewsAsync("recent-near", _t.AddMinutes(-1), instruments: ["NQ"]); // in the window, near the star
        await SeedNewsAsync("recent-far", _t, instruments: ["NQ"]);                 // newest, in the window, NOT near
        await RateAsync("star-seed");

        (string, float[])[] embeddings =
        [
            ("star-seed", new[] { 1f, 0f }),
            ("recent-near", new[] { 1f, 0f }), // same direction as the star
            ("recent-far", new[] { 0f, 1f }),  // orthogonal
            .. Enumerable.Range(0, 5).Select(i => ($"old-near-{i}", new[] { 1f, 0f })),
        ];
        SemanticSalienceAxis axis = Axis(available: true, vectors: Vectors(embeddings));

        // Window of 2: only the two most-recent items (recent-far, recent-near); the five older near items are excluded.
        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(2, read, _options, axis, default));

        feed.Items.Should().HaveCount(2);
        NewsFeedItemResponse recent = feed.Items.Single(item => item.DedupKey == "recent-near");
        recent.Multiplier.Should().BeGreaterThan(1.0);
        recent.Reasons.Should().Contain(reason => reason.Dimension == nameof(SalienceDimension.SemanticEmbedding));
    }

    [Fact]
    public async Task Feed_DegradesToCategoricalOnly_WhenTheSemanticAxisIsUnavailable()
    {
        // An unavailable axis (the gh#109 degrade) yields an empty map -- the feed is exactly its pre-#853,
        // categorical-only self: a categorical star still boosts, and no item carries a SemanticEmbedding reason.
        await SeedNewsAsync("es-seed", _t.AddDays(-1), instruments: ["ES"]);
        await SeedNewsAsync("es-fresh", _t, instruments: ["ES"]);
        await RateAsync("es-seed");

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, Axis(), default));

        NewsFeedItemResponse boosted = feed.Items.Single(item => item.DedupKey == "es-fresh");
        boosted.Multiplier.Should().BeGreaterThan(1.0); // categorical star still boosts
        feed.Items.Should().OnlyContain(item =>
            item.Reasons.All(reason => reason.Dimension != nameof(SalienceDimension.SemanticEmbedding)));
    }

    // ---- R-20 isolation ----

    [Fact]
    public async Task OneOperatorsStars_DoNotTouchAnothersFeed()
    {
        await SeedNewsAsync("es-fresh", _t, instruments: ["ES"]);
        await SeedNewsAsync("es-seed", _t.AddDays(-1), instruments: ["ES"]);
        await RateAsync("es-seed", asUser: _operator); // operator stars ES

        // The OTHER operator has no feedback, so their ES item sits at base (multiplier 1) with no own-feedback.
        await using TradingCopilotDbContext read = Context(_other);
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, Axis(), default));

        NewsFeedItemResponse fresh = feed.Items.Single(item => item.DedupKey == "es-fresh");
        fresh.Multiplier.Should().Be(1.0);
        fresh.Feedback.Should().BeNull();
    }

    [Fact]
    public async Task AnOperator_CannotClearAnothersFeedback()
    {
        await SeedNewsAsync("item", _t);
        await RateAsync("item", asUser: _operator);

        await using TradingCopilotDbContext asOther = Context(_other);
        IResult result = await NewsEndpoints.ClearFeedbackAsync("item", null, asOther, default);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound); // invisible to the other operator (R-20)
    }
}
