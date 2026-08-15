using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.IntegrationTests.Ai;

/// <summary>
/// Independent QA for gh#888 (paired with gh#881, sub-issue of gh#763) — the embedding by-owner / nearest reads
/// filtering by the <b>current</b> model (<see cref="PgVectorNewsSimilarity"/>), plus the model-transition
/// self-heal a re-embed under a new model is supposed to provide. Relational-only
/// (<see cref="EmbeddingRecord"/> is <c>Ignore()</c>d off the in-memory provider, gh#109), so this needs real
/// Postgres + pgvector (Testcontainers) — the analogue of #855 / #877.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the spec, gh#881 is unimplemented as of this suite.</b> As of when this suite was written,
/// gh#881 (the coding task that is meant to add a <c>Model == provider.Model</c> predicate to
/// <see cref="PgVectorNewsSimilarity.ReadVectorsAsync"/> / <c>NearestNewsAsync</c>) carried an open claim branch
/// with zero commits and no linked PR — <see cref="PgVectorNewsSimilarity"/> takes only a
/// <see cref="TradingCopilotDbContext"/>, no <see cref="IEmbeddingProvider"/>, and its by-owner read filters
/// solely on <c>(OwnerKind, OwnerId)</c>. Per the QA guard discipline (rule 3), this suite is not blocked on that
/// landing: every case below is written to the intended (post-gh#881) behaviour, and where today's code cannot
/// meet it the OBSERVED (broken) behaviour is pinned per rule 2, citing gh#881 — each pinned assertion is this
/// task's own regression guard until gh#881 lands, at which point it flips to the commented intended assertion.
/// </para>
/// <para>
/// <b>Prove-red-first, done by temporarily applying gh#881's intended fix locally.</b> Since there is no existing
/// predicate to revert, red was proven the other direction: a <c>Model == provider.Model</c> predicate (with
/// <see cref="IEmbeddingProvider"/> constructor-injected) was added to a local, uncommitted copy of
/// <see cref="PgVectorNewsSimilarity"/>, confirming every pinned-defect assertion below flips red for exactly the
/// reason named (and the commented intended assertions flip green) — then discarded; only the pinned suite as
/// authored here is committed. See the PR body for the exact local diff and command transcript.
/// </para>
/// <para>
/// <b>The doubled provider.</b> <see cref="EmbeddingProviderDoubleTestPostgresFactory"/> substitutes
/// <see cref="IEmbeddingProvider"/> with <see cref="AdversarialEmbeddingProvider"/> (mirroring
/// <c>AdversarialLlmProvider</c> for the LLM outbound seam): Cohere cannot exist pre-merge (no key, no egress),
/// and the model-transition case needs ONE host to move its reported "current model" mid-run, which a real
/// <c>CohereEmbeddingProvider</c> (its model snapshotted once from <c>IOptions</c> at host build) cannot do. The
/// double feeds a vector and reports a model; it never computes a filter or candidacy decision — that stays
/// production code (<see cref="PgVectorNewsSimilarity"/>, <see cref="NewsEmbeddingService"/>) under test.
/// </para>
/// </remarks>
public sealed class NewsEmbeddingModelScopedReadIntegrationTests : IClassFixture<EmbeddingProviderDoubleTestPostgresFactory>
{
    private const string CurrentModel = "cohere-embed-v3-model-scoped-test";
    private const string OldModel = "cohere-embed-v2-model-scoped-test";
    private static readonly DateTimeOffset _recordedAt = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly EmbeddingProviderDoubleTestPostgresFactory _factory;

    public NewsEmbeddingModelScopedReadIntegrationTests(EmbeddingProviderDoubleTestPostgresFactory factory)
    {
        _factory = factory;
        // Each test owns the store AND the double's state: the container is shared across the class (one per
        // suite, gh#121), so start every test from empty rather than accumulating rows / stale call history
        // across cases -- mirrors the sibling gh#855/gh#861 suites over the same base factory.
        _factory.Provider.Reset();
        _factory.Provider.Model = CurrentModel;
        ResetAsync().GetAwaiter().GetResult();
    }

    // =================================================================================================================
    // By-owner reads must filter by the CURRENT model -- acceptance criterion 1.
    // =================================================================================================================

    [Fact]
    public async Task GetVectorsAsync_ShouldReturnOnlyTheCurrentModelVector_WhenAnOlderModelRowAlsoExistsForTheSameOwner()
    {
        await SeedAsync(
            Row(EmbeddingOwnerKind.SoftSignal, "a", CurrentModel, Direction((0, 1f))),
            Row(EmbeddingOwnerKind.SoftSignal, "a", OldModel, Direction((1, 1f))));

        IReadOnlyList<StoredEmbedding> hits = await GetVectorsAsync(["a"]);

        // DEFECT gh#881: PgVectorNewsSimilarity.ReadVectorsAsync filters only on (OwnerKind, OwnerId) today, so a
        // stale row from a retired model still surfaces alongside the current one -- the exact "duplicate-owner
        // rows" hazard gh#881's own issue body names. Once gh#881's Model == provider.Model predicate lands, this
        // should read `hits.Should().ContainSingle().Which.Vector.Should().Equal(Direction((0, 1f)).ToArray())`
        // (the CurrentModel vector only) -- this pinned count is that fix's own regression guard.
        hits.Should().HaveCount(2,
            "DEFECT gh#881: the by-owner read is not yet scoped to the current model, so BOTH the current-model "
            + "and the stale old-model row for owner \"a\" are returned instead of exactly one");
        hits.Select(hit => hit.OwnerId).Should().OnlyContain(id => id == "a");
    }

    [Fact]
    public async Task GetTopicVectorsAsync_ShouldReturnOnlyTheCurrentModelVector_WhenAnOlderModelRowAlsoExistsForTheSameTopic()
    {
        await SeedAsync(
            Row(EmbeddingOwnerKind.Topic, "t", CurrentModel, Direction((0, 1f))),
            Row(EmbeddingOwnerKind.Topic, "t", OldModel, Direction((1, 1f))));

        IReadOnlyList<StoredEmbedding> hits = await GetTopicVectorsAsync(["t"]);

        // DEFECT gh#881: same hazard as the SoftSignal case above, over GetTopicVectorsAsync's shared
        // ReadVectorsAsync helper. Once fixed: `hits.Should().ContainSingle().Which.Vector.Should().Equal(...)`.
        hits.Should().HaveCount(2,
            "DEFECT gh#881: the topic by-owner read is not yet scoped to the current model, so BOTH the "
            + "current-model and the stale old-model row for topic \"t\" are returned instead of exactly one");
        hits.Select(hit => hit.OwnerId).Should().OnlyContain(id => id == "t");
    }

    // =================================================================================================================
    // Nearest-N must exclude other-model rows against the REAL production seam -- acceptance criterion 2. The
    // sibling assertion inside NewsEmbeddingPgvectorReadIntegrationTests was rewired to this same real seam in
    // this PR (see that file's remarks); this case additionally proves the double-provider host reaches the
    // identical INewsEmbeddingSimilarity.NearestNewsAsync entry point other-model exclusion depends on.
    // =================================================================================================================

    [Fact]
    public async Task NearestNewsAsync_ShouldExcludeOtherModelRows_ViaTheRealProductionSeam()
    {
        // The old-model row is the CLOSEST possible vector to the query (identical direction) -- if the Model
        // filter were present it could never rank; today, with no filter at all, it is the top hit.
        await SeedAsync(
            Row(EmbeddingOwnerKind.SoftSignal, "wrong-model-but-identical-vector", OldModel, Direction((0, 1f))),
            Row(EmbeddingOwnerKind.SoftSignal, "right-model", CurrentModel, Direction((0, 0.8f), (1, 0.6f))));

        IReadOnlyList<SemanticNeighbor> hits = await NearestNewsAsync(Direction((0, 1f)).ToArray(), n: 5);

        // DEFECT gh#881: NearestNewsAsync filters only on OwnerKind == SoftSignal today, so the old-model row
        // (closer by construction) outranks and is included alongside the current-model answer. Once gh#881
        // lands this should read `hits.Select(h => h.OwnerId).Should().Equal(["right-model"])` -- current-model
        // only, and this pinned ordering is that fix's own regression guard.
        hits.Select(hit => hit.OwnerId).Should().Equal(
            ["wrong-model-but-identical-vector", "right-model"],
            "DEFECT gh#881: nearest-N is not yet scoped to the current model, so the closer old-model row leads "
            + "the current-model answer instead of being excluded");
    }

    // =================================================================================================================
    // Cross-kind + cross-model isolation -- acceptance criterion 4. Split into the two invariants the criterion
    // names, kept SEPARATE on purpose: mixing an old-model row into the kind-isolation assertion would make that
    // assertion's own truth depend on whether gh#881's model filter has landed (a current-model-only read making
    // an old-model row vanish is the CORRECT post-fix behaviour criterion 1 pins, not a kind leak) -- entangling
    // the two would make this case flip for the wrong reason the moment gh#881 lands. So: (a) below proves the
    // OwnerKind filter -- already correct today (gh#852/gh#853/gh#854), independently of gh#881 -- never crosses
    // kind boundaries, with every row under the SAME current model so this half is a genuine PASSING regression
    // guard both today and after gh#881 lands; (b) the cross-MODEL half of the matrix is what criteria 1-3 above
    // already prove (a SoftSignal-vs-SoftSignal and Topic-vs-Topic model mismatch each pinned there) -- together
    // the full kind x model matrix is covered without one assertion contradicting another's intended fix.
    // =================================================================================================================

    [Fact]
    public async Task ByOwnerReads_ShouldNotBleedAcrossOwnerKind_EvenWhenBothKindsShareTheCurrentModel()
    {
        await SeedAsync(
            Row(EmbeddingOwnerKind.Topic, "topic-x", CurrentModel, Direction((0, 1f))),
            Row(EmbeddingOwnerKind.SoftSignal, "news-y", CurrentModel, Direction((1, 1f))));

        IReadOnlyList<StoredEmbedding> topicHits = await GetTopicVectorsAsync(["topic-x"]);
        IReadOnlyList<StoredEmbedding> softSignalHits = await GetVectorsAsync(["news-y"]);

        topicHits.Should().ContainSingle("the Topic row must surface from the Topic read").Which.OwnerId.Should().Be("topic-x");
        softSignalHits.Should().ContainSingle("the SoftSignal row must surface from the SoftSignal read").Which.OwnerId.Should().Be("news-y");

        // Requesting the OTHER kind's owner id from each read must come back empty -- the cross-kind leak this
        // case exists to guard.
        (await GetTopicVectorsAsync(["news-y"])).Should().BeEmpty("a SoftSignal-owned id must never surface from a Topic read");
        (await GetVectorsAsync(["topic-x"])).Should().BeEmpty("a Topic-owned id must never surface from a SoftSignal read");
    }

    // =================================================================================================================
    // Model-transition self-heal -- acceptance criterion 3. Seed one owner embedded ONLY under the OLD model; with
    // the provider on the NEW model, the by-owner read should degrade (empty); one EmbedPendingAsync pass should
    // re-embed it under the new model; the read should then surface the new-model vector.
    // =================================================================================================================

    [Fact]
    public async Task ModelTransition_ShouldSelfHeal_WhenEmbedPendingAsyncRunsAfterTheProviderMovesToANewModel()
    {
        const string ownerId = "gh888-model-transition-story";
        NewsRecord news = new()
        {
            DedupKey = ownerId,
            Type = "news",
            Url = "https://example.com/model-transition-story",
            Title = "gh#888 model-transition story",
            Summary = "A story embedded under an old model before the operator moved to a new one.",
            PublishedAt = _recordedAt,
            Tickers = [],
            SourceFeeds = ["test"],
            RecordedAt = _recordedAt,
        };

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            TradingCopilotDbContext seedDatabase = seedScope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
            seedDatabase.News.Add(news);
            await seedDatabase.SaveChangesAsync();
        }

        // Phase 1: embed under the OLD model -- the deployment's state BEFORE the operator changes Cohere:Model.
        _factory.Provider.Model = OldModel;
        int embeddedUnderOldModel = await RunEmbedPendingPassAsync(_recordedAt.AddMinutes(1));
        embeddedUnderOldModel.Should().Be(1, "the seeded story has no embedding yet, so the first pass must embed it");

        List<EmbeddingRecord> afterFirstPass = await ReadAllEmbeddingsAsync(ownerId);
        EmbeddingRecord oldModelRow = afterFirstPass.Should().ContainSingle("only the old-model embed has happened so far").Which;
        oldModelRow.Model.Should().Be(OldModel);

        // Phase 2: the operator moves the deployment's current model forward. No re-embed has run yet.
        _factory.Provider.Model = "cohere-embed-v3-model-scoped-test-transitioned";
        string newModel = _factory.Provider.Model;

        IReadOnlyList<StoredEmbedding> beforeReembed = await GetVectorsAsync([ownerId]);

        // DEFECT gh#881: the by-owner read is not yet scoped to the current model, so the STALE old-model vector
        // still surfaces even though the deployment has already moved on -- it should degrade to empty instead.
        // Once gh#881 lands this should read `beforeReembed.Should().BeEmpty(...)`.
        beforeReembed.Should().ContainSingle(
            "DEFECT gh#881: the by-owner read still surfaces the stale old-model vector after the deployment's "
            + "current model has moved on, instead of degrading to empty")
            .Which.Vector.Should().Equal(oldModelRow.Embedding.ToArray(),
                "the surfaced vector is exactly the stale OLD-model one, not a coincidental match");

        // Phase 3: one embedding pass under the NEW model -- the self-heal this suite exists to prove. The
        // candidacy query's NOT EXISTS(Model == newModel) predicate makes the story a re-embed candidate again
        // (production NewsEmbeddingService logic, untouched by gh#881), regardless of the by-owner read's own
        // defect proven above.
        int embeddedUnderNewModel = await RunEmbedPendingPassAsync(_recordedAt.AddMinutes(2));
        embeddedUnderNewModel.Should().Be(1, "the story has no NEW-model embedding yet, so this pass must re-embed it");

        List<EmbeddingRecord> afterSecondPass = await ReadAllEmbeddingsAsync(ownerId);
        EmbeddingRecord newModelRow = afterSecondPass.Should().Contain(row => row.Model == newModel,
            "the re-embed pass must have written a row under the deployment's NEW current model").Which;
        newModelRow.Embedding.ToArray().Should().NotBeEmpty("the re-embed must have stored a real vector, not a placeholder");

        // The self-heal genuinely happened at the WRITE side -- proven directly against the database, independent
        // of the by-owner read's own gh#881 defect.
        afterSecondPass.Should().Contain(row => row.Model == OldModel,
            "DEFECT gh#881 (issue body item 1): the stale old-model row is left behind, not swept -- a "
            + "model transition leaves duplicate-owner rows until gh#881's sweep or read-filter lands");

        // DEFECT gh#881: the by-owner read, called again after the re-embed, now returns BOTH rows (old + new
        // model) for the same owner instead of exactly the new-model one -- the duplicate-owner-rows hazard
        // gh#881's issue body names as its first symptom. Once fixed: `.Should().ContainSingle().Which.Vector...`.
        IReadOnlyList<StoredEmbedding> afterReembed = await GetVectorsAsync([ownerId]);
        afterReembed.Should().HaveCount(2,
            "DEFECT gh#881: after the re-embed, the by-owner read returns BOTH the old-model and new-model rows "
            + "for the same owner instead of exactly the new-model one");
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

    private static EmbeddingRecord Row(EmbeddingOwnerKind ownerKind, string ownerId, string model, Vector embedding) => new()
    {
        OwnerKind = ownerKind,
        OwnerId = ownerId,
        Model = model,
        Dimensions = TradingCopilotDbContext.EmbeddingDimensions,
        Embedding = embedding,
        ContentHash = "gh888-test-content-hash",
        RecordedAt = _recordedAt,
    };

    private async Task SeedAsync(params EmbeddingRecord[] rows)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Embeddings.AddRange(rows);
        await database.SaveChangesAsync();
    }

    // The three reads below each resolve INewsEmbeddingSimilarity -- the REAL DI-registered seam, exactly what
    // NewsSemanticSearch/relevance consume in production -- from a throwaway scope, so a fix landing in
    // PgVectorNewsSimilarity is what this suite actually observes rather than a private in-suite query.

    private async Task<IReadOnlyList<StoredEmbedding>> GetVectorsAsync(IReadOnlyCollection<string> ownerIds)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        INewsEmbeddingSimilarity similarity = scope.ServiceProvider.GetRequiredService<INewsEmbeddingSimilarity>();
        return await similarity.GetVectorsAsync(ownerIds, CancellationToken.None);
    }

    private async Task<IReadOnlyList<StoredEmbedding>> GetTopicVectorsAsync(IReadOnlyCollection<string> topicNames)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        INewsEmbeddingSimilarity similarity = scope.ServiceProvider.GetRequiredService<INewsEmbeddingSimilarity>();
        return await similarity.GetTopicVectorsAsync(topicNames, CancellationToken.None);
    }

    private async Task<IReadOnlyList<SemanticNeighbor>> NearestNewsAsync(IReadOnlyList<float> queryVector, int n)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        INewsEmbeddingSimilarity similarity = scope.ServiceProvider.GetRequiredService<INewsEmbeddingSimilarity>();
        return await similarity.NearestNewsAsync(queryVector, n, CancellationToken.None);
    }

    private async Task<int> RunEmbedPendingPassAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        NewsEmbeddingService service = scope.ServiceProvider.GetRequiredService<NewsEmbeddingService>();
        return await service.EmbedPendingAsync(now, CancellationToken.None);
    }

    private async Task<List<EmbeddingRecord>> ReadAllEmbeddingsAsync(string ownerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.Embeddings
            .Where(embedding => embedding.OwnerKind == EmbeddingOwnerKind.SoftSignal && embedding.OwnerId == ownerId)
            .ToListAsync();
    }

    private async Task ResetAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Embeddings.ExecuteDeleteAsync();
        await database.News.ExecuteDeleteAsync();
        await database.NewsTopics.ExecuteDeleteAsync();
    }
}
