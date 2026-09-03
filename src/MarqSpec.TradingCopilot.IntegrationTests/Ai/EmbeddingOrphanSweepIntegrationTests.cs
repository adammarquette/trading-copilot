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
/// Independent QA for gh#902 (paired with QA card gh#914, per the embedding tree's dev+QA pairing convention) —
/// the orphaned-owner embedding GC: a renamed <see cref="NewsTopic"/> or a deleted <see cref="NewsRecord"/> leaves
/// an <see cref="EmbeddingRecord"/> behind with no producer, and <see cref="EmbeddingOrphanGcHost.SweepAsync"/>'s
/// set-based anti-join <c>DELETE</c> is what reclaims it. The delete is relational-only — the polymorphic
/// <c>Embeddings</c> store is <c>Ignore()</c>d off the in-memory provider (gh#109, ADR-0001) — so it is exercisable
/// only against real Postgres, over <see cref="EmbeddingReadTestPostgresFactory"/> (shared with the sibling
/// gh#888/gh#889 suites; every always-on <see cref="Microsoft.Extensions.Hosting.IHostedService"/> is stripped so
/// the real <c>EmbeddingOrphanGcHost</c>'s own wall-clock background loop cannot race a test's explicit
/// <see cref="RunSweepAsync"/> call).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from gh#914's acceptance criteria, not from the implementation.</b> gh#902 landed on its own claim
/// branch (<c>feat/902_embedding-orphan-gc</c>, PR #917) in parallel with this suite, per the board's dev+QA
/// pairing convention — QA work advances independently and is not blocked on the paired dev PR merging first. The
/// production branch was read <b>only</b> to learn the compiled shape this suite must target
/// (<see cref="IEmbeddingOrphanStore"/>, <see cref="EmbeddingOrphanGcHost.SweepAsync"/>,
/// <see cref="EmbeddingOrphanSweep.SweepableKinds"/>) — every assertion below still comes from the gh#914 issue
/// body alone, never from "whatever the branch currently does". See the gh#914 PR description for this suite's
/// build/green status against that branch as of submission.
/// </para>
/// <para>
/// <b>Why every case drives <see cref="EmbeddingOrphanGcHost.SweepAsync"/> rather than the store directly.</b> That
/// static method — not <see cref="IEmbeddingOrphanStore.DeleteOrphansAsync"/> in isolation — is "the sweep" the
/// gh#914 acceptance criteria name throughout ("the sweep deletes it", "a second sweep deletes nothing"): it is
/// the one place <see cref="EmbeddingOrphanSweep.SweepableKinds"/> is consulted to decide which owner kinds even
/// get a <c>DeleteOrphansAsync</c> call, so it is the only entry point that can prove a producer-less kind is kept
/// out of the sweep <b>entirely</b> rather than merely refused by the store's own switch. Every case still exercises
/// the real anti-join <c>DELETE</c> underneath, resolved from DI exactly as <c>EmbeddingOrphanGcHost</c> resolves
/// it in production (a fresh scope per pass).
/// </para>
/// <para>
/// <b>Verified green against the real gh#902 branch, and every guard proven red on the defect it names, before
/// this PR opened.</b> This authoring sandbox's egress policy blocks the Docker Hub registry blob CDN, so
/// Testcontainers cannot pull any image here (CI has no such restriction and runs the real
/// <see cref="EmbeddingReadTestPostgresFactory"/> path). Pending that: every case above was run, unmodified, over
/// an apt-installed native Postgres 16 + pgvector 0.6.0 standing in for the container — the same substitution the
/// sibling gh#855 suite's remarks document for the identical constraint — and passed 6/6 against
/// <c>feat/902_embedding-orphan-gc</c> (commit <c>86c68ed</c>). Each guard was then run again against a
/// deliberately broken local copy of <see cref="IEmbeddingOrphanStore.DeleteOrphansAsync"/> — a no-op flips
/// <see cref="Sweep_ShouldDeleteARenamedTopicOrphan_AndLeaveTheCurrentNameRowUntouched"/>,
/// <see cref="Sweep_ShouldDeleteADeletedNewsSoftSignalOrphan"/> and
/// <see cref="Sweep_ShouldBeIdempotent_ASecondPassDeletesNothing"/> red; an unconditional delete (the anti-join
/// dropped) flips <see cref="Sweep_ShouldNeverTouchALiveOwner_UnderAnyModel"/> and
/// <see cref="Sweep_ShouldNeverRaceAConcurrentEmbedOfAStillLiveOwner"/> red; adding a producer-less kind to the
/// allow-list AND wiring it to delete unconditionally flips
/// <see cref="Sweep_ShouldNeverTouchProducerlessOwnerKinds"/> red — each for exactly the reason named. The broken
/// copies never touched the branch; only this file is committed.
/// </para>
/// <para>
/// <b>Aligned by gh#1065.</b> <c>Suggestion</c> gained a producer (<c>Suggestions.Id</c>) and moved onto the
/// allow-list alongside the new <c>JournalEntry</c> kind, so it is no longer an example of a producer-less kind and
/// its row here would now be correctly swept. Only that one case's subject changed; the claim — an allow-list keeps
/// a kind with no producer out of the sweep entirely, rather than GC'ing it to zero because its owner "cannot be
/// found" — is unchanged, and <c>Rule</c> / <c>MarketSnapshot</c> still carry it.
/// </para>
/// </remarks>
public sealed class EmbeddingOrphanSweepIntegrationTests : IClassFixture<EmbeddingReadTestPostgresFactory>
{
    private const string ModelA = "gh914-orphan-sweep-model-a";
    private const string ModelB = "gh914-orphan-sweep-model-b";
    private static readonly DateTimeOffset _recordedAt = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly EmbeddingReadTestPostgresFactory _factory;

    public EmbeddingOrphanSweepIntegrationTests(EmbeddingReadTestPostgresFactory factory)
    {
        _factory = factory;
        // The container is shared across the class (one per suite, gh#121): start every test from empty rather
        // than accumulating rows across cases, mirroring the sibling gh#888/gh#889 suites over the same factory.
        ResetAsync().GetAwaiter().GetResult();
    }

    // =================================================================================================================
    // Acceptance criterion 1 — a renamed Topic orphan is swept; the current-name row survives.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldDeleteARenamedTopicOrphan_AndLeaveTheCurrentNameRowUntouched()
    {
        // NewsTopic naming is delete+insert (OwnerId = NewsTopic.Name), so a rename leaves the OLD name's
        // EmbeddingRecord orphaned -- no NewsTopic named "fomc-old" exists any more.
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, "fomc-old", ModelA);
        await SeedTopicAsync("fomc-renamed");
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, "fomc-renamed", ModelA);

        int deleted = await RunSweepAsync();

        deleted.Should().Be(1, "exactly the old-name orphan should be swept");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Topic, "fomc-old")).Should().BeEmpty(
            "the renamed-away Topic name has no producing NewsTopic left, so its embedding must be reclaimed");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Topic, "fomc-renamed")).Should().ContainSingle(
            "the current-name row's NewsTopic still exists, so the sweep must leave it alone");
    }

    // =================================================================================================================
    // Acceptance criterion 2 — a deleted-news SoftSignal orphan is swept.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldDeleteADeletedNewsSoftSignalOrphan()
    {
        // OwnerId = NewsRecord.DedupKey; no NewsRecord with this dedup key exists (e.g. pruned after ingestion),
        // so its SoftSignal embedding has no producer.
        const string deletedDedupKey = "gh914-pruned-story";
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, deletedDedupKey, ModelA);

        int deleted = await RunSweepAsync();

        deleted.Should().Be(1, "the orphaned SoftSignal row for the pruned story should be swept");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.SoftSignal, deletedDedupKey)).Should().BeEmpty(
            "no NewsRecord carries this dedup key any more, so the embedding must be reclaimed");
    }

    // =================================================================================================================
    // Acceptance criterion 3 — a live owner is never touched, under any model.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldNeverTouchALiveOwner_UnderAnyModel()
    {
        const string liveDedupKey = "gh914-live-story";
        const string liveTopicName = "gh914-live-topic";

        await SeedNewsAsync(liveDedupKey);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, liveDedupKey, ModelA);
        // A second, differently-modeled row for the SAME live owner: orphan-sweep cares only about owner
        // existence, never about model staleness (that is gh#889's separate concern) -- so this must ALSO survive.
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, liveDedupKey, ModelB);

        await SeedTopicAsync(liveTopicName);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, liveTopicName, ModelA);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, liveTopicName, ModelB);

        int deleted = await RunSweepAsync();

        deleted.Should().Be(0, "every seeded row belongs to a still-existing owner, so nothing should be swept");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.SoftSignal, liveDedupKey)).Should().HaveCount(
            2, "both model rows of the live SoftSignal owner must survive");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Topic, liveTopicName)).Should().HaveCount(
            2, "both model rows of the live Topic owner must survive");
    }

    // =================================================================================================================
    // Acceptance criterion 4 — producer-less owner kinds are never touched, even though their owner "cannot be
    // found" (there is no producer table at all to check against). EmbeddingOrphanSweep's allow-list must keep
    // them out of the sweep entirely -- proven by driving the real orchestrator (EmbeddingOrphanGcHost.SweepAsync),
    // which is the one place that allow-list is consulted, rather than the store in isolation.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldNeverTouchProducerlessOwnerKinds()
    {
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Rule, "gh914-orphan-rule", ModelA);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.MarketSnapshot, "gh914-orphan-snapshot", ModelA);

        int deleted = await RunSweepAsync();

        deleted.Should().Be(0, "no sweepable-kind row was seeded, so the sweep must report nothing deleted");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Rule, "gh914-orphan-rule")).Should().ContainSingle(
            "Rule has no producer table yet (the rulebook is epic gh#15) -- the allow-list must keep it out of the "
            + "sweep entirely, never GC'd to zero just because its owner 'cannot be found'");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.MarketSnapshot, "gh914-orphan-snapshot")).Should().ContainSingle(
            "MarketSnapshot has no producer table yet -- same allow-list guarantee");
    }

    // =================================================================================================================
    // Acceptance criterion 5 (part 1) — idempotent: a second sweep deletes nothing.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldBeIdempotent_ASecondPassDeletesNothing()
    {
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Topic, "gh914-idempotent-orphan", ModelA);
        await SeedNewsAsync("gh914-idempotent-live");
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, "gh914-idempotent-live", ModelA);

        int firstPass = await RunSweepAsync();
        firstPass.Should().Be(1, "the first pass must sweep exactly the seeded orphan");

        int secondPass = await RunSweepAsync();
        secondPass.Should().Be(0, "the orphan is already gone, so a second pass must find and delete nothing");

        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.SoftSignal, "gh914-idempotent-live")).Should().ContainSingle(
            "the live owner must remain untouched across both passes");
    }

    // =================================================================================================================
    // Acceptance criterion 5 (part 2) — concurrency-safe: a concurrent embed of a still-live owner is never raced
    // away. The owner's producer is committed BEFORE the race begins and never removed, so NOT EXISTS is false for
    // it throughout -- this does NOT prove atomicity/TOCTOU-safety (that needs a fluctuating producer, which this
    // fixture does not exercise). What it does prove: dropping DeleteOrphansAsync's anti-join predicate (an
    // unconditional delete) flips this test red, so it is a genuine regression guard against that defect, plus a
    // concurrent-load smoke test -- 25 simultaneous sweep passes racing one insert on separate DI
    // scopes/connections against real Postgres, not a simulated interleave.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldNeverRaceAConcurrentEmbedOfAStillLiveOwner()
    {
        const string ownerId = "gh914-concurrent-embed-owner";
        await SeedNewsAsync(ownerId); // the producer exists BEFORE the race starts and is never removed.

        Task embed = SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, ownerId, ModelA);
        Task<int>[] sweeps = [.. Enumerable.Range(0, 25).Select(_ => RunSweepAsync())];

        await Task.WhenAll([embed, .. sweeps]);

        List<EmbeddingRecord> remaining = await ReadEmbeddingsAsync(EmbeddingOwnerKind.SoftSignal, ownerId);
        remaining.Should().ContainSingle(
            "the owner existed before the race began and was never removed, so the concurrently-embedded row must "
            + "survive every concurrent sweep pass -- a raced anti-join is exactly the hazard gh#902's own design "
            + "note names ('a concurrent embed pass can re-add an owner mid-GC; the delete must not race a "
            + "legitimate re-embed')");
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    private static Vector Zero() => new(new float[TradingCopilotDbContext.EmbeddingDimensions]);

    private async Task SeedEmbeddingAsync(EmbeddingOwnerKind ownerKind, string ownerId, string model)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Embeddings.Add(new EmbeddingRecord
        {
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            Model = model,
            Dimensions = TradingCopilotDbContext.EmbeddingDimensions,
            Embedding = Zero(),
            ContentHash = "gh914-test-content-hash",
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
            Title = $"gh#914 story {dedupKey}",
            Summary = "A story used to prove the orphan-GC's live-owner path.",
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
            Keywords = ["gh914"],
            Scope = TopicScope.Global,
        });
        await database.SaveChangesAsync();
    }

    /// <summary>
    /// Runs one full sweep pass through the real orchestrator over the real, DI-resolved
    /// <see cref="IEmbeddingOrphanStore"/> -- a fresh scope per call, exactly as <see cref="EmbeddingOrphanGcHost"/>
    /// does per pass in production.
    /// </summary>
    private async Task<int> RunSweepAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEmbeddingOrphanStore store = scope.ServiceProvider.GetRequiredService<IEmbeddingOrphanStore>();
        // gh#915 widened SweepAsync with a current-model arg for the stale-model backstop; null runs the
        // orphaned-owner sweep ONLY, which is exactly what these gh#902 cases assert. The backstop has its own
        // integration coverage (#920).
        return await EmbeddingOrphanGcHost.SweepAsync(store, currentModel: null, NullLogger.Instance, CancellationToken.None);
    }

    private async Task<List<EmbeddingRecord>> ReadEmbeddingsAsync(EmbeddingOwnerKind ownerKind, string ownerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.Embeddings
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
