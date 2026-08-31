using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Relevance;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace MarqSpec.TradingCopilot.IntegrationTests.Ai;

/// <summary>
/// Independent QA for gh#920 (the paired QA card for gh#915, per the embedding tree's dev+QA pairing convention)
/// — the crash-leaked stale-model backstop: <see cref="IEmbeddingOrphanStore.DeleteStaleModelDuplicatesAsync"/>,
/// a self-<c>EXISTS</c> <c>DELETE</c> over the polymorphic <c>Embeddings</c> table. Relational-only, like every
/// suite in this tree (gh#109, ADR-0001) — <see cref="EmbeddingRecord"/>'s <c>Vector</c> column has no
/// in-memory-provider mapping — so this needs real Postgres, over <see cref="EmbeddingReadTestPostgresFactory"/>
/// (shared with the sibling gh#902/gh#914 orphan-sweep suite; every always-on
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> is stripped so the real host's own wall-clock
/// background loop cannot race a test's explicit <see cref="RunSweepAsync"/> call).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the gh#920 issue body, not from the implementation.</b> gh#915 (the production backstop) was
/// already merged to <c>develop</c> when this suite was written (PR #921) — pure QA-tier coverage of shipped
/// code, per the board's dev+QA pairing convention. Every assertion traces to gh#920's own "What to verify" list.
/// </para>
/// <para>
/// <b>Takes over an abandoned claim.</b> <c>feature/920_stale-model-backstop-qa</c> existed but its one commit was
/// residue from an unrelated gh#914 review round, not gh#920 coverage, and no PR was ever opened from it — see
/// the PR description for this suite for the takeover note. This suite lands on the shared gh#877 claim branch
/// instead, since both issues sit in the same polymorphic <c>Embeddings</c> tree.
/// </para>
/// <para>
/// <b>Every seeded owner in the stale-model cases is a LIVE owner</b> — a matching <see cref="NewsRecord"/> or
/// <see cref="NewsTopic"/> is seeded alongside every <see cref="EmbeddingRecord"/> here, so the orphaned-owner
/// sweep (gh#902, which <see cref="EmbeddingOrphanGcHost.SweepAsync"/> runs first, in the SAME call) contributes
/// zero to the returned count — otherwise a case's "exactly N stale-model rows deleted" assertion could not tell
/// the two sweeps' deletions apart.
/// </para>
/// <para>
/// <b>The "skipped with no provider" case holds partly by construction.</b> <c>DeleteStaleModelDuplicatesAsync</c>
/// takes a non-nullable <c>string currentModel</c> (a compile-time guard on its own), and the self-<c>EXISTS</c>
/// predicate compares against the <c>Embeddings.Model</c> column, which is itself <c>NOT NULL</c> — so even a
/// query with no outer guard at all can never match a null <c>currentModel</c> against a real row. The genuine
/// regression guard for "the call never happens" is therefore the existing unit suite
/// (<c>EmbeddingOrphanGcHostTests.SweepAsync_ShouldSkipTheStaleModelBackstop_WhenNoCurrentModelIsConfigured</c>,
/// a mocked-store <c>MustNotHaveHappened()</c> assertion) — this suite's
/// <see cref="Sweep_ShouldTouchNothing_WhenNoCurrentModelIsConfigured"/> is the integration-tier corroboration
/// that passing <c>currentModel: null</c> is safe end to end against real data that a real current model WOULD
/// otherwise sweep, not an independent proof that the call is skipped.
/// </para>
/// </remarks>
public sealed class EmbeddingStaleModelBackstopIntegrationTests : IClassFixture<EmbeddingReadTestPostgresFactory>
{
    private const string CurrentModel = "gh920-stale-backstop-current";
    private const string StaleModel = "gh920-stale-backstop-stale";
    private static readonly DateTimeOffset _recordedAt = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly EmbeddingReadTestPostgresFactory _factory;

    public EmbeddingStaleModelBackstopIntegrationTests(EmbeddingReadTestPostgresFactory factory)
    {
        _factory = factory;
        // The container is shared across the class (one per suite, gh#121): start every test from empty rather
        // than accumulating rows across cases, mirroring the sibling gh#914 suite over the same base factory.
        ResetAsync().GetAwaiter().GetResult();
    }

    // =================================================================================================================
    // Acceptance criterion 1 -- a crash-leaked stale duplicate is swept: an owner with BOTH a current-model row
    // and a Model != current row has its stale-model row deleted and its current-model row kept. Proven for both
    // owner kinds the store actually holds vectors for.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldDeleteTheStaleModelRow_AndKeepTheCurrentModelRow_ForASoftSignalOwner()
    {
        const string ownerId = "gh920-soft-signal-duplicate";
        await SeedNewsAsync(ownerId); // a LIVE owner -- keeps the orphan sweep from contributing to the count
        Vector currentVector = Direction((0, 1f));
        Vector staleVector = Direction((1, 1f));
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, ownerId, CurrentModel, currentVector);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, ownerId, StaleModel, staleVector);

        int deleted = await RunSweepAsync(CurrentModel);

        deleted.Should().Be(1, "exactly the stale-model duplicate should be swept");
        List<EmbeddingRecord> remaining = await ReadEmbeddingsAsync(EmbeddingOwnerKind.SoftSignal, ownerId);
        remaining.Should().ContainSingle("the current-model sibling must survive")
            .Which.Model.Should().Be(CurrentModel);
        remaining[0].Embedding.ToArray().Should().Equal(currentVector.ToArray(), "the surviving row must be the untouched current-model one");
    }

    [Fact]
    public async Task Sweep_ShouldDeleteTheStaleModelRow_AndKeepTheCurrentModelRow_ForATopicOwner()
    {
        const string ownerId = "gh920-topic-duplicate";
        await SeedTopicAsync(ownerId); // a LIVE owner
        Vector currentVector = Direction((0, 1f));
        Vector staleVector = Direction((1, 1f));
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, ownerId, CurrentModel, currentVector);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, ownerId, StaleModel, staleVector);

        int deleted = await RunSweepAsync(CurrentModel);

        deleted.Should().Be(1, "exactly the stale-model duplicate should be swept");
        List<EmbeddingRecord> remaining = await ReadEmbeddingsAsync(EmbeddingOwnerKind.Topic, ownerId);
        remaining.Should().ContainSingle("the current-model sibling must survive")
            .Which.Model.Should().Be(CurrentModel);
        remaining[0].Embedding.ToArray().Should().Equal(currentVector.ToArray(), "the surviving row must be the untouched current-model one");
    }

    // =================================================================================================================
    // Acceptance criterion 2 -- an owner's SOLE vector is never touched: an owner with only a Model != current row
    // (no current-model sibling) is left alone -- no duplicate, so nothing to sweep.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldNeverTouchAnOwnersSoleVector_WhenNoCurrentModelSiblingExists()
    {
        const string soleSoftSignalOwner = "gh920-sole-vector-soft-signal";
        const string soleTopicOwner = "gh920-sole-vector-topic";
        await SeedNewsAsync(soleSoftSignalOwner);
        await SeedTopicAsync(soleTopicOwner);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, soleSoftSignalOwner, StaleModel, Direction((0, 1f)));
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, soleTopicOwner, StaleModel, Direction((0, 1f)));

        int deleted = await RunSweepAsync(CurrentModel);

        deleted.Should().Be(0, "neither owner has a current-model sibling, so there is no duplicate to resolve");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.SoftSignal, soleSoftSignalOwner)).Should().ContainSingle(
            "an owner not yet re-embedded under the current model must keep its only vector, whatever its model");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Topic, soleTopicOwner)).Should().ContainSingle(
            "same guarantee for a Topic owner -- the backstop is owner-kind-agnostic");
    }

    // =================================================================================================================
    // Acceptance criterion 3 -- no over-reach: the sweep needs no allow-list, deletes no current-model row, and
    // touches no unrelated owner, across owner kinds. One pass, several owners in flight together, so a query that
    // is too broad (e.g. missing the per-owner correlation) would show up as cross-owner damage here.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldNotOverreach_AcrossSeveralOwnersAndBothOwnerKinds_InOnePass()
    {
        const string duplicateOwner = "gh920-overreach-duplicate";
        const string soleOwner = "gh920-overreach-sole";
        const string currentOnlyOwner = "gh920-overreach-current-only";
        const string duplicateTopicOwner = "gh920-overreach-duplicate-topic";

        await SeedNewsAsync(duplicateOwner);
        await SeedNewsAsync(soleOwner);
        await SeedNewsAsync(currentOnlyOwner);
        await SeedTopicAsync(duplicateTopicOwner);

        Vector currentVector = Direction((0, 1f));
        Vector staleVector = Direction((1, 1f));

        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, duplicateOwner, CurrentModel, currentVector);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, duplicateOwner, StaleModel, staleVector);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, soleOwner, StaleModel, staleVector);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, currentOnlyOwner, CurrentModel, currentVector);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, duplicateTopicOwner, CurrentModel, currentVector);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, duplicateTopicOwner, StaleModel, staleVector);

        int deleted = await RunSweepAsync(CurrentModel);

        deleted.Should().Be(2, "exactly the two owners with a genuine stale/current pair contribute a deletion");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.SoftSignal, duplicateOwner)).Should().ContainSingle()
            .Which.Model.Should().Be(CurrentModel, "the duplicate owner's current-model row must survive");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.SoftSignal, soleOwner)).Should().ContainSingle()
            .Which.Model.Should().Be(StaleModel, "the sole-vector owner must be untouched, current-model row or not");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.SoftSignal, currentOnlyOwner)).Should().ContainSingle()
            .Which.Model.Should().Be(CurrentModel, "a current-model-only owner (no stale sibling at all) must never be touched");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Topic, duplicateTopicOwner)).Should().ContainSingle()
            .Which.Model.Should().Be(CurrentModel, "the Topic duplicate owner's current-model row must survive too -- owner-kind-agnostic");
    }

    // =================================================================================================================
    // Acceptance criterion 4 -- skipped with no provider: with no current model, the backstop does not run. See
    // the class remarks for what this integration-tier case can and cannot prove on its own.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldTouchNothing_WhenNoCurrentModelIsConfigured()
    {
        const string ownerId = "gh920-no-provider-duplicate";
        await SeedNewsAsync(ownerId);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, ownerId, CurrentModel, Direction((0, 1f)));
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, ownerId, StaleModel, Direction((1, 1f)));

        int deleted = await RunSweepAsync(currentModel: null);

        deleted.Should().Be(0, "with no current model known, the stale-model backstop must not run at all");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.SoftSignal, ownerId)).Should().HaveCount(
            2, "the SAME pair a real current model would have swept to one row must be left fully intact");
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

    private async Task SeedEmbeddingAsync(EmbeddingOwnerKind ownerKind, string ownerId, string model, Vector embedding)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Embeddings.Add(new EmbeddingRecord
        {
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            Model = model,
            Dimensions = TradingCopilotDbContext.EmbeddingDimensions,
            Embedding = embedding,
            ContentHash = "gh920-test-content-hash",
            RecordedAt = _recordedAt,
        });
        await database.SaveChangesAsync();
    }

    private async Task SeedNewsAsync(string dedupKey)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.News.Add(new NewsRecord
        {
            DedupKey = dedupKey,
            Type = "news",
            Url = $"https://example.com/{dedupKey}",
            Title = $"gh#920 story {dedupKey}",
            Summary = "A story used to keep a SoftSignal owner live for the stale-model backstop suite.",
            PublishedAt = _recordedAt,
            Tickers = [],
            SourceFeeds = ["test"],
            RecordedAt = _recordedAt,
        });
        await database.SaveChangesAsync();
    }

    private async Task SeedTopicAsync(string name)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.NewsTopics.Add(new NewsTopic
        {
            Name = name,
            Keywords = ["gh920"],
            Scope = TopicScope.Global,
        });
        await database.SaveChangesAsync();
    }

    /// <summary>
    /// Runs one full pass through the real orchestrator over the real, DI-resolved <see cref="IEmbeddingOrphanStore"/>
    /// -- a fresh scope per call, exactly as <see cref="EmbeddingOrphanGcHost"/> does per pass in production. The
    /// orphaned-owner sweep (gh#902) always runs first; every owner seeded in this suite is live (see class
    /// remarks), so it never contributes to the returned count.
    /// </summary>
    private async Task<int> RunSweepAsync(string? currentModel)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEmbeddingOrphanStore store = scope.ServiceProvider.GetRequiredService<IEmbeddingOrphanStore>();
        return await EmbeddingOrphanGcHost.SweepAsync(store, currentModel, NullLogger.Instance, CancellationToken.None);
    }

    private async Task<List<EmbeddingRecord>> ReadEmbeddingsAsync(EmbeddingOwnerKind ownerKind, string ownerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.Embeddings
            .AsNoTracking()
            .Where(embedding => embedding.OwnerKind == ownerKind && embedding.OwnerId == ownerId)
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
