using MarqSpec.TradingCopilot.Api.Relevance;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Relevance;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;

namespace MarqSpec.TradingCopilot.IntegrationTests.Relevance;

/// <summary>
/// Independent QA for gh#877 (of gh#763, paired with the already-merged gh#854 dev card) — the end-to-end half of
/// semantic topic match: <see cref="NewsRelevanceService.ResolveAsync"/> materializing a topic onto a news item's
/// <c>MatchedTopics</c> by cosine proximity alone, composing with the pre-existing keyword path. The embed-pass
/// and by-owner-read half of the same issue lives in the sibling <c>Ai/NewsEmbeddingTopicMatchIntegrationTests</c>
/// suite. Relational-only (gh#109, ADR-0001): the vectors this pass reads have no in-memory-provider mapping, so
/// this needs real Postgres + pgvector (Testcontainers).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the gh#877 issue body, not from the resolver's implementation.</b> gh#854 was already merged
/// to <c>develop</c> when this suite was written (dev+QA pairing convention); the production source
/// (<see cref="NewsRelevanceResolver.Resolve"/>, its <c>DefaultSemanticThreshold</c>) was read only to learn the
/// compiled shape this suite must target, not to derive expected values from it — every seeded vector pair below
/// is constructed independently, by <b>geometry</b> (identical or orthogonal basis directions), so the expected
/// cosine similarity is known by construction rather than computed by calling the production similarity function.
/// </para>
/// <para>
/// <b>Vectors are seeded directly, not produced by a real embed pass.</b> The seam under test here is the READ +
/// RESOLVE path (<see cref="NewsRelevanceService.ResolveAsync"/> reading back stored vectors via
/// <see cref="Domain.Ai.INewsEmbeddingSimilarity"/> and feeding them to the pure resolver), not the embed pass
/// that produces those vectors (the sibling suite's job). Seeding <see cref="EmbeddingRecord"/> rows directly with
/// hand-built basis vectors makes the expected cosine similarity exact and deterministic — a real provider's
/// vectors are unpredictable, opaque floats that could never prove "clears the 0.75 threshold" versus "falls just
/// short" on purpose. The doubled <see cref="Domain.Ai.IEmbeddingProvider"/> (<see cref="AdversarialEmbeddingProvider"/>,
/// via <see cref="EmbeddingProviderDoubleTestPostgresFactory"/>) only needs to report <c>IsAvailable = true</c> and
/// the same <c>Model</c> the seeded rows carry, so the reads are not short-circuited by the gh#109 degrade.
/// </para>
/// <para>
/// <b>Keywords are chosen to never appear in the news text</b> for every semantic-match case, so a passing
/// assertion cannot be secretly explained by the pre-existing keyword path — the semantic path is what is on
/// trial. The keyword-only case makes the opposite choice on purpose (topic never embedded near anything), so the
/// suite proves both paths compose in the SAME pass rather than one crowding out the other.
/// </para>
/// </remarks>
public sealed class SemanticTopicMatchIntegrationTests : IClassFixture<EmbeddingProviderDoubleTestPostgresFactory>
{
    private const string CurrentModel = "gh877-semantic-match-model";
    private static readonly DateTimeOffset _now = new(2026, 8, 30, 15, 0, 0, TimeSpan.Zero);

    private readonly EmbeddingProviderDoubleTestPostgresFactory _factory;

    public SemanticTopicMatchIntegrationTests(EmbeddingProviderDoubleTestPostgresFactory factory)
    {
        _factory = factory;
        _factory.Provider.Reset();
        _factory.Provider.Model = CurrentModel;
        ResetAsync().GetAwaiter().GetResult();
    }

    // =================================================================================================================
    // Acceptance criterion -- end-to-end semantic match: a topic embedded near a news item, whose keywords are
    // absent from the news text, materializes onto MatchedTopics through the semantic path alone. Composed with a
    // NEGATIVE control (a topic embedded FAR from the news item, also with no keyword overlap) in the SAME pass,
    // so the test can actually fail -- a resolver that matched everything regardless of distance would pass the
    // positive half vacuously without the negative half alongside it.
    // =================================================================================================================

    [Fact]
    public async Task ResolveAsync_ShouldMaterializeATopic_ViaSemanticMatchAlone_WhenItsKeywordsAreAbsentFromTheText()
    {
        Vector sharedDirection = Direction((0, 1f));
        Vector orthogonalDirection = Direction((1, 1f));

        await SeedTopicAsync("gh877-semantic-near", TopicScope.Global, instrument: null, "nonexistent-keyword-alpha");
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, "gh877-semantic-near", sharedDirection);

        await SeedTopicAsync("gh877-semantic-far", TopicScope.Global, instrument: null, "nonexistent-keyword-beta");
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, "gh877-semantic-far", orthogonalDirection);

        await SeedNewsAsync(NewsSeed(
            "gh877-semantic-story", title: "Quarterly outlook", summary: "Nothing here mentions either topic's keywords."));
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, "gh877-semantic-story", sharedDirection);

        await ResolveAsync();

        NewsRecord row = await ReadNewsAsync("gh877-semantic-story");
        row.MatchedTopics.Should().Contain(
            "gh877-semantic-near",
            "the news vector is IDENTICAL in direction to this topic's vector (cosine similarity 1.0, well above "
            + "the 0.75 threshold), and its keyword never appears in the text -- this can only be the semantic path");
        row.MatchedTopics.Should().NotContain(
            "gh877-semantic-far",
            "this topic's vector is ORTHOGONAL to the news vector (cosine similarity 0.0) and its keyword is also "
            + "absent -- the negative control proving the match above is not vacuous");
    }

    // =================================================================================================================
    // Acceptance criterion -- while a purely semantic match is proven above, a keyword-only topic (never embedded
    // near the item, or not embedded at all) must still match in the SAME pass -- the semantic addition must not
    // crowd out the pre-existing keyword path.
    // =================================================================================================================

    [Fact]
    public async Task ResolveAsync_ShouldStillMatchAKeywordOnlyTopic_InTheSamePassAsASemanticMatch()
    {
        Vector sharedDirection = Direction((0, 1f));
        Vector orthogonalDirection = Direction((1, 1f));

        await SeedTopicAsync("gh877-semantic-only", TopicScope.Global, instrument: null, "nonexistent-keyword-gamma");
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, "gh877-semantic-only", sharedDirection);

        // Keyword-only: its own vector is nowhere near the story (orthogonal), so it can match ONLY by keyword.
        await SeedTopicAsync("gh877-keyword-only", TopicScope.Global, instrument: null, "earnings call");
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, "gh877-keyword-only", orthogonalDirection);

        await SeedNewsAsync(NewsSeed(
            "gh877-composed-story", title: "Q2 update", summary: "The quarterly earnings call beat estimates."));
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, "gh877-composed-story", sharedDirection);

        await ResolveAsync();

        NewsRecord row = await ReadNewsAsync("gh877-composed-story");
        row.MatchedTopics.Should().BeEquivalentTo(
            ["gh877-semantic-only", "gh877-keyword-only"],
            "the semantic match (near vector, absent keyword) and the keyword match (far vector, present keyword) "
            + "must both surface from the SAME resolve pass -- neither path crowds out the other");
    }

    // =================================================================================================================
    // Acceptance criterion -- a semantically-matched instrument-scoped topic attaches its Instrument to
    // MatchedInstruments, exactly as the pre-existing keyword-driven instrument attachment does (gh#361).
    // =================================================================================================================

    [Fact]
    public async Task ResolveAsync_ShouldAttachTheInstrument_WhenASemanticallyMatchedTopicIsInstrumentScoped()
    {
        Vector sharedDirection = Direction((0, 1f));

        await SeedTopicAsync(
            "gh877-oil-inventory", TopicScope.Instrument, instrument: "CL", "nonexistent-keyword-delta");
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, "gh877-oil-inventory", sharedDirection);

        await SeedNewsAsync(NewsSeed(
            "gh877-instrument-story", title: "Energy markets", summary: "A larger-than-expected draw was reported."));
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, "gh877-instrument-story", sharedDirection);

        await ResolveAsync();

        NewsRecord row = await ReadNewsAsync("gh877-instrument-story");
        row.MatchedTopics.Should().Contain("gh877-oil-inventory", "the semantic match itself must still fire");
        row.MatchedInstruments.Should().Contain(
            "CL", "an instrument-scoped topic matched purely semantically must still attach its instrument, "
            + "exactly as a keyword-driven match does (gh#361) -- this is a second path to the SAME attachment, "
            + "not a different one");
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    private static Vector Direction(params (int Index, float Weight)[] weights)
    {
        float[] values = new float[TradingCopilotDbContext.EmbeddingDimensions];
        foreach ((int index, float weight) in weights)
        {
            values[index] = weight;
        }

        return new Vector(values);
    }

    private static NewsRecord NewsSeed(string dedupKey, string title, string summary) => new()
    {
        DedupKey = dedupKey,
        Type = "news",
        Url = $"https://example.com/{dedupKey}",
        Title = title,
        Summary = summary,
        PublishedAt = _now.AddMinutes(-5),
        Tickers = [],
        SourceFeeds = ["test"],
        RecordedAt = _now.AddMinutes(-5),
    };

    private Task SeedNewsAsync(NewsRecord news) =>
        ExecuteDbContextAsync(async database =>
        {
            database.News.Add(news);
            await database.SaveChangesAsync();
        });

    private Task SeedTopicAsync(string name, TopicScope scope, string? instrument, params string[] keywords) =>
        ExecuteDbContextAsync(async database =>
        {
            database.NewsTopics.Add(new NewsTopic
            {
                Id = Guid.NewGuid(),
                Name = name,
                Keywords = [.. keywords],
                Scope = scope,
                Instrument = instrument,
            });
            await database.SaveChangesAsync();
        });

    private Task SeedEmbeddingAsync(EmbeddingOwnerKind ownerKind, string ownerId, Vector embedding) =>
        ExecuteDbContextAsync(async database =>
        {
            database.Embeddings.Add(new EmbeddingRecord
            {
                OwnerKind = ownerKind,
                OwnerId = ownerId,
                Model = CurrentModel,
                Dimensions = TradingCopilotDbContext.EmbeddingDimensions,
                Embedding = embedding,
                ContentHash = "gh877-semantic-test-content-hash",
                RecordedAt = _now,
            });
            await database.SaveChangesAsync();
        });

    private async Task ResolveAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        NewsRelevanceService service = scope.ServiceProvider.GetRequiredService<NewsRelevanceService>();
        await service.ResolveAsync(_now, CancellationToken.None);
    }

    private Task<NewsRecord> ReadNewsAsync(string dedupKey) =>
        QueryDbContextAsync(database => database.News.AsNoTracking().SingleAsync(n => n.DedupKey == dedupKey));

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

    private Task ResetAsync() =>
        ExecuteDbContextAsync(async database =>
        {
            await database.Embeddings.ExecuteDeleteAsync();
            await database.News.ExecuteDeleteAsync();
            await database.NewsTopics.ExecuteDeleteAsync();
            await database.RelevanceConfigStates.ExecuteDeleteAsync();
        });
}
