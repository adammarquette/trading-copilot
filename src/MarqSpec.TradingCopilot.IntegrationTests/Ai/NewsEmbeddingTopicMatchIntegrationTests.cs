using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Relevance;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;

namespace MarqSpec.TradingCopilot.IntegrationTests.Ai;

/// <summary>
/// Independent QA for gh#877 (of gh#763, paired with the already-merged gh#854 dev card) — the topic-embedding
/// half of semantic topic match: <see cref="NewsEmbeddingService.EmbedPendingAsync"/>'s topic pass, and
/// <see cref="INewsEmbeddingSimilarity.GetTopicVectorsAsync"/> / <c>GetVectorsAsync</c>'s cross-kind isolation
/// over the shared <c>ReadVectorsAsync</c> helper. The end-to-end semantic-match-onto-<c>MatchedTopics</c> half
/// lives in the sibling <c>Relevance/SemanticTopicMatchIntegrationTests</c> suite. Relational-only, like every
/// suite in this tree (gh#109, ADR-0001): <see cref="EmbeddingRecord"/>'s <c>Vector</c> column has no
/// in-memory-provider mapping, so this needs real Postgres + pgvector (Testcontainers).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the gh#877 issue body, not from <see cref="NewsEmbeddingService"/>'s implementation.</b> Both
/// gh#854 (topic embed + resolver) and gh#915 (the stale-model backstop this tree's sibling suite covers) were
/// already merged to <c>develop</c> when this suite was written — this is pure QA-tier coverage of shipped code,
/// per the board's dev+QA pairing convention. The production source was read only to learn the compiled shape
/// this suite must target (<see cref="NewsEmbeddingService.EmbedPendingAsync"/>, <see cref="INewsEmbeddingSimilarity"/>,
/// <see cref="AdversarialEmbeddingProvider"/>); every assertion below traces to the issue's own acceptance list.
/// </para>
/// <para>
/// <b>The doubled provider.</b> Reuses <see cref="EmbeddingProviderDoubleTestPostgresFactory"/> (shared with the
/// sibling gh#888 suite) — Cohere cannot exist pre-merge (no key, no egress), so <see cref="IEmbeddingProvider"/>
/// is doubled with <see cref="AdversarialEmbeddingProvider"/>, which feeds a deterministic per-text vector and
/// records every text handed to it (<c>EmbeddedTexts</c>) — the mechanism this suite uses to prove "no second
/// paid embed call" directly, rather than inferring it from a side effect.
/// </para>
/// <para>
/// <b>A documented divergence from the issue's literal wording (not a defect).</b> The issue's idempotency bullet
/// says an unchanged pass leaves the row with "<c>RecordedAt</c> touched only". <see cref="NewsEmbeddingService"/>'s
/// topic pass does not do that — its own code comment explains why: unlike news (whose candidate query filters on
/// <c>RecordedAt</c>, so an untouched row would be re-checked forever), the topic candidate set is <i>every</i>
/// topic every pass, so there is nothing to "touch out" of a future query, and the row is left byte-identical
/// instead. The two invariants that actually matter — no new/changed vector, no second paid call — hold either
/// way, and are what <see cref="EmbedPendingAsync_ShouldBeIdempotent_WhenTopicIsUnchanged_AndMakeNoSecondPaidEmbedCall"/>
/// guards; that test asserts the OBSERVED (and, per the production comment, deliberate) untouched-<c>RecordedAt</c>
/// behaviour rather than the issue's literal phrase, so the guard matches reality rather than enshrining a
/// misreading of it.
/// </para>
/// </remarks>
public sealed class NewsEmbeddingTopicMatchIntegrationTests : IClassFixture<EmbeddingProviderDoubleTestPostgresFactory>
{
    private const string CurrentModel = "gh877-topic-match-model";
    private static readonly DateTimeOffset _now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly EmbeddingProviderDoubleTestPostgresFactory _factory;

    public NewsEmbeddingTopicMatchIntegrationTests(EmbeddingProviderDoubleTestPostgresFactory factory)
    {
        _factory = factory;
        // Each test owns the store AND the double's call history: the container is shared across the class
        // (one per suite, gh#121), so start every test from empty -- mirrors the sibling gh#888 suite.
        _factory.Provider.Reset();
        _factory.Provider.Model = CurrentModel;
        ResetAsync().GetAwaiter().GetResult();
    }

    // =================================================================================================================
    // Acceptance criterion 1 -- the news-embedding pass embeds a seeded NewsTopic as an EmbeddingRecord with
    // OwnerKind = Topic, OwnerId = <topic name>, content = name + "\n\n" + join(' ', keywords), Model / Dimensions /
    // ContentHash populated. Also locks the schema fact the issue names: Topic = 5 persists cleanly.
    // =================================================================================================================

    [Fact]
    public async Task EmbedPendingAsync_ShouldEmbedASeededTopic_WithComposedContentAndOwnerKindTopic()
    {
        ((int)EmbeddingOwnerKind.Topic).Should().Be(5, "the issue names this exact schema fact -- Topic is owner kind 5");

        await SeedTopicAsync("gh877-fomc", ["rate", "hike"]);
        string expectedContent = "gh877-fomc\n\nrate hike"; // name + "\n\n" + join(' ', keywords), independently composed

        int embedded = await RunEmbedPendingPassAsync();

        embedded.Should().Be(1, "the one seeded topic has no embedding yet");
        _factory.Provider.EmbeddedTexts.Should().Equal(
            [expectedContent], "the exact composed content must be what is handed to the provider");

        EmbeddingRecord row = await ReadTopicEmbeddingAsync("gh877-fomc");
        row.OwnerKind.Should().Be(EmbeddingOwnerKind.Topic);
        row.OwnerId.Should().Be("gh877-fomc");
        row.Model.Should().Be(CurrentModel);
        row.Dimensions.Should().Be(TradingCopilotDbContext.EmbeddingDimensions);
        row.ContentHash.Should().Be(EmbeddingContentHash.For(expectedContent));
        row.Embedding.ToArray().Should().NotBeEquivalentTo(
            new float[TradingCopilotDbContext.EmbeddingDimensions], "a real vector must be stored, not a placeholder");
    }

    // =================================================================================================================
    // Acceptance criterion 2 -- idempotent: a second pass over an unchanged topic writes no new/changed vector and
    // makes no second paid embed call (see the class remarks for the RecordedAt divergence from the issue's wording).
    // =================================================================================================================

    [Fact]
    public async Task EmbedPendingAsync_ShouldBeIdempotent_WhenTopicIsUnchanged_AndMakeNoSecondPaidEmbedCall()
    {
        await SeedTopicAsync("gh877-idempotent", ["steady", "state"]);
        const string content = "gh877-idempotent\n\nsteady state";

        int firstPass = await RunEmbedPendingPassAsync();
        firstPass.Should().Be(1);
        EmbeddingRecord afterFirstPass = await ReadTopicEmbeddingAsync("gh877-idempotent");

        int secondPass = await RunEmbedPendingPassAsync();

        secondPass.Should().Be(0, "the topic's content has not changed, so there is nothing left to (re-)embed");
        _factory.Provider.EmbeddedTexts.Count(text => text == content).Should().Be(
            1, "an unchanged topic must never be paid-embedded a second time");

        EmbeddingRecord afterSecondPass = await ReadTopicEmbeddingAsync("gh877-idempotent");
        afterSecondPass.ContentHash.Should().Be(afterFirstPass.ContentHash, "the hash must not change for unchanged content");
        afterSecondPass.Embedding.ToArray().Should().Equal(
            afterFirstPass.Embedding.ToArray(), "the stored vector must be byte-identical -- no re-embed happened");
        afterSecondPass.RecordedAt.Should().Be(
            afterFirstPass.RecordedAt,
            "the topic pass leaves an unchanged row completely untouched (see class remarks) -- unlike news, "
            + "the topic candidate query has no RecordedAt predicate to drop the row out of, so there is nothing "
            + "to gain by re-writing RecordedAt on every pass");
    }

    // =================================================================================================================
    // Acceptance criterion 3 -- a topic whose keywords changed re-embeds (ContentHash differs -> new vector).
    // =================================================================================================================

    [Fact]
    public async Task EmbedPendingAsync_ShouldReembedTopic_WhenKeywordsChange()
    {
        await SeedTopicAsync("gh877-keywords-change", ["initial", "keywords"]);
        int firstPass = await RunEmbedPendingPassAsync();
        firstPass.Should().Be(1);
        EmbeddingRecord beforeChange = await ReadTopicEmbeddingAsync("gh877-keywords-change");

        await UpdateTopicKeywordsAsync("gh877-keywords-change", ["revised", "wording"]);
        int secondPass = await RunEmbedPendingPassAsync();

        secondPass.Should().Be(1, "the topic's keywords changed, so it must be re-embedded");
        _factory.Provider.EmbeddedTexts.Should().Contain(
            "gh877-keywords-change\n\nrevised wording", "the NEW composed content must be what is handed to the provider");

        EmbeddingRecord afterChange = await ReadTopicEmbeddingAsync("gh877-keywords-change");
        afterChange.ContentHash.Should().NotBe(beforeChange.ContentHash, "the hash must change for changed content");
        afterChange.Embedding.ToArray().Should().NotEqual(
            beforeChange.Embedding.ToArray(), "a genuinely new vector must be stored for the changed content");
        afterChange.RecordedAt.Should().Be(_now, "a genuine re-embed DOES stamp RecordedAt, unlike the unchanged case above");
    }

    // =================================================================================================================
    // Acceptance criterion 3 (continued) -- a topic whose NAME changed re-embeds. OwnerId is the topic's name, so
    // a rename creates a NEW EmbeddingRecord under the new name (the old name's row becoming an orphan is gh#902's
    // concern, already covered by the sibling EmbeddingOrphanSweepIntegrationTests suite -- not re-asserted here).
    // =================================================================================================================

    [Fact]
    public async Task EmbedPendingAsync_ShouldReembedTopic_WhenNameChanges_ByCreatingANewOwnerRow()
    {
        await SeedTopicAsync("gh877-old-name", ["stable", "keywords"]);
        int firstPass = await RunEmbedPendingPassAsync();
        firstPass.Should().Be(1);

        await UpdateTopicNameAsync("gh877-old-name", "gh877-new-name");
        int secondPass = await RunEmbedPendingPassAsync();

        secondPass.Should().Be(1, "the renamed topic has no EmbeddingRecord under its NEW name yet");
        EmbeddingRecord newNameRow = await ReadTopicEmbeddingAsync("gh877-new-name");
        newNameRow.ContentHash.Should().Be(EmbeddingContentHash.For("gh877-new-name\n\nstable keywords"));
    }

    // =================================================================================================================
    // Acceptance criteria 4 + 5 -- GetTopicVectorsAsync returns only Topic rows, never SoftSignal; and the
    // SoftSignal read (GetVectorsAsync) is unaffected by Topic rows -- the shared ReadVectorsAsync refactor's
    // cross-kind isolation, proven with a DELIBERATELY OVERLAPPING owner id so a kind leak in either direction
    // would surface as the wrong vector, not merely an extra row.
    // =================================================================================================================

    [Fact]
    public async Task GetTopicVectorsAsync_ShouldReturnOnlyTopicRows_AndGetVectorsAsync_ShouldBeUnaffectedByThem()
    {
        const string sharedOwnerId = "gh877-shared-owner-id";
        Vector topicVector = Direction((0, 1f));
        Vector softSignalVector = Direction((1, 1f));

        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, sharedOwnerId, topicVector);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, sharedOwnerId, softSignalVector);

        IReadOnlyList<StoredEmbedding> topicHits = await GetTopicVectorsAsync([sharedOwnerId]);
        IReadOnlyList<StoredEmbedding> softSignalHits = await GetVectorsAsync([sharedOwnerId]);

        topicHits.Should().ContainSingle("only the Topic row must surface from the Topic read")
            .Which.Vector.Should().Equal(topicVector.ToArray(), "the Topic read must return the TOPIC vector, never the SoftSignal one");
        softSignalHits.Should().ContainSingle(
                "the SoftSignal read (#853's salience read) must be unaffected by the Topic row sharing this owner id")
            .Which.Vector.Should().Equal(softSignalVector.ToArray(), "the SoftSignal read must return the SOFTSIGNAL vector, never the Topic one");
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

    private Task SeedTopicAsync(string name, IReadOnlyList<string> keywords) =>
        ExecuteDbContextAsync(async database =>
        {
            database.NewsTopics.Add(new NewsTopic
            {
                Id = Guid.NewGuid(),
                Name = name,
                Keywords = [.. keywords],
                Scope = TopicScope.Global,
            });
            await database.SaveChangesAsync();
        });

    private Task UpdateTopicKeywordsAsync(string name, IReadOnlyList<string> keywords) =>
        ExecuteDbContextAsync(async database =>
        {
            NewsTopic topic = await database.NewsTopics.SingleAsync(t => t.Name == name);
            topic.Keywords = [.. keywords];
            await database.SaveChangesAsync();
        });

    private Task UpdateTopicNameAsync(string oldName, string newName) =>
        ExecuteDbContextAsync(async database =>
        {
            NewsTopic topic = await database.NewsTopics.SingleAsync(t => t.Name == oldName);
            topic.Name = newName;
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
                ContentHash = "gh877-test-content-hash",
                RecordedAt = _now,
            });
            await database.SaveChangesAsync();
        });

    private async Task<int> RunEmbedPendingPassAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        NewsEmbeddingService service = scope.ServiceProvider.GetRequiredService<NewsEmbeddingService>();
        return await service.EmbedPendingAsync(_now, CancellationToken.None);
    }

    private async Task<EmbeddingRecord> ReadTopicEmbeddingAsync(string ownerId) =>
        await QueryDbContextAsync(database => database.Embeddings.AsNoTracking().SingleAsync(
            embedding => embedding.OwnerKind == EmbeddingOwnerKind.Topic && embedding.OwnerId == ownerId));

    private async Task<IReadOnlyList<StoredEmbedding>> GetTopicVectorsAsync(IReadOnlyCollection<string> topicNames)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        INewsEmbeddingSimilarity similarity = scope.ServiceProvider.GetRequiredService<INewsEmbeddingSimilarity>();
        return await similarity.GetTopicVectorsAsync(topicNames, CancellationToken.None);
    }

    private async Task<IReadOnlyList<StoredEmbedding>> GetVectorsAsync(IReadOnlyCollection<string> ownerIds)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        INewsEmbeddingSimilarity similarity = scope.ServiceProvider.GetRequiredService<INewsEmbeddingSimilarity>();
        return await similarity.GetVectorsAsync(ownerIds, CancellationToken.None);
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

    private Task ResetAsync() =>
        ExecuteDbContextAsync(async database =>
        {
            await database.Embeddings.ExecuteDeleteAsync();
            await database.News.ExecuteDeleteAsync();
            await database.NewsTopics.ExecuteDeleteAsync();
        });
}
