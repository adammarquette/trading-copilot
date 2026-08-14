using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.IntegrationTests.Ai;

/// <summary>
/// Independent QA for gh#855 (sub-issue of gh#763, epic gh#14 [D2]) — the pgvector similarity read that
/// <c>NewsEmbeddingServiceTests</c> (unit tier) proves is <b>reachable but untestable</b> on the in-memory provider:
/// <c>TradingCopilotDbContext</c> <c>Ignore()</c>s <see cref="EmbeddingRecord"/> unless <c>Database.IsNpgsql()</c>
/// (gh#109), so the <c>CosineDistance</c> nearest-N query, the HNSW cosine index built by the
/// <c>AddEmbeddingStore</c> migration, and the composite-key identity behind <c>OwnerId</c> exist only against a
/// real Postgres — which is exactly what this suite, over <see cref="EmbeddingReadTestPostgresFactory"/>, provides.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the spec, not the implementation.</b> gh#852 (the query-side similarity seam this card pairs
/// with) is unimplemented as of this suite — no production code anywhere calls <c>CosineDistance</c> yet. Per the
/// QA contract this suite does not wait on it: the acceptance criteria name the entity mapping and the raw
/// <c>CosineDistance</c> query directly, so the query below (<see cref="NearestAsync"/>) is this suite's own
/// independent exercise of that read — the same query gh#852's seam will wrap once it lands, at which point this
/// suite is its regression guard.
/// </para>
/// <para>
/// <b>Topic-embedding read — deliberately out of scope.</b> The issue's "if stored, cover that read too" clause is
/// conditional on gh#854 (semantic topic match), which has not landed: <see cref="EmbeddingOwnerKind"/> carries no
/// <c>Topic</c> member and no topic-embedding write path exists yet in <c>NewsTopic</c>. There is no read to prove
/// against real Postgres because there is no column and no writer — inventing one here would be gh#854's job done
/// from the wrong contract (QA does not touch <c>src/**</c> outside this project). Deferred to gh#854.
/// </para>
/// <para>
/// <b>gh#858 — a real defect this suite found, blocking 7 of 8 cases (guard discipline #3).</b> CI's real
/// Testcontainers run (a genuinely fresh Postgres container, migrated in-process) hit every <c>Vector</c>-writing
/// case with the SAME failure: <c>NotSupportedException: Cannot resolve 'vector' to a fully qualified datatype
/// name. The datatype was not found in the current database info.</c> Root cause (filed as gh#858):
/// <c>StartupTasks.MigrateAndBootstrapAsync</c>'s <c>MigrateAsync()</c> opens the process's FIRST Npgsql
/// connection — which is also what runs <c>AddEmbeddingStore</c>'s <c>CREATE EXTENSION vector</c> — so Npgsql's
/// per-data-source type catalog is cached from BEFORE the extension existed, and nothing calls
/// <c>ReloadTypesAsync()</c> afterward; every later <c>Vector</c> write from that same process (i.e. this whole
/// test run) hits the stale cache. This is a genuine production gap (it would equally break
/// <c>NewsEmbeddingService</c>'s first upsert after any deployment's first-ever boot against a brand-new
/// database), not a suite defect — per the guard discipline it was reported (gh#858), not fixed in the QA suite.
/// gh#858's fix (this change) makes <c>StartupTasks</c> reload the Npgsql type catalog after the in-process
/// <c>CREATE EXTENSION</c> — so the cases below, originally marked <c>Skip = "blocked by gh#858"</c>, are now
/// un-skipped and are its regression guard. Only
/// <see cref="Migration_ShouldHaveEnabledPgvector_AndBuiltTheHnswCosineIndex"/> (a raw-SQL read that writes no
/// <c>Vector</c>) was ever unaffected — its own pass, isolating the failure to writes only, is what made the root
/// cause identifiable in the first place.
/// </para>
/// <para>
/// <b>Why the pre-commit local verification (below) did not surface this.</b> Before ever discovering CI's
/// result, this suite's query shape and its three adversarial guards were verified against an apt-installed
/// native Postgres 16 + <c>pgvector</c> 0.6.0 (no container pull needed, since this sandbox's egress policy
/// blocks every container-registry CDN <c>Testcontainers.PostgreSql</c> needs — confirmed a policy block, not a
/// config issue, since unrelated pulls like <c>mcr.microsoft.com</c> succeeded). That harness ran
/// <c>CREATE EXTENSION vector</c> via a SEPARATE <c>psql</c> process, before the C# harness's own
/// <c>NpgsqlDataSource</c> ever opened its first connection — so its type catalog was built AFTER the extension
/// already existed, sidestepping gh#858's exact window by construction. In hindsight this is itself corroborating
/// evidence for the root cause: the defect is specific to "the extension is created BY the same process that
/// then writes the type," which only a from-scratch, migrate-in-process container run (CI's, or
/// <see cref="EmbeddingReadTestPostgresFactory"/>'s) reproduces. Against that native instance: all 8 cases passed
/// as committed; swapping the nearest-N query's <c>OrderBy</c> to <c>OrderByDescending</c> flipped
/// <c>NearestN_ShouldOrderByCosineDistanceAscending_*</c> red (the farthest row led instead of the nearest);
/// dropping the <c>OwnerKind</c> predicate let the wrong-owner-kind row (identical vector, closest possible
/// distance) leak into the top hit and flipped <c>NearestN_ShouldExcludeOtherOwnerKinds_*</c> red; dropping the
/// <c>Model</c> predicate did the same to <c>NearestN_ShouldExcludeOtherModels_*</c> — each break flipped exactly
/// and only its own case, and all three were restored and reconfirmed green. That remains real evidence the
/// query logic itself (ordering, filter-before-rank) is correct; gh#858 is a separate, earlier-in-the-pipeline
/// bootstrap gap that keeps CI from reaching it right now.
/// </para>
/// </remarks>
public sealed class NewsEmbeddingPgvectorReadIntegrationTests : IClassFixture<EmbeddingReadTestPostgresFactory>
{
    private const string Model = "cohere-embed-v3-test";
    private const string OtherModel = "cohere-embed-v2-test";
    private static readonly DateTimeOffset _recordedAt = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly EmbeddingReadTestPostgresFactory _factory;

    public NewsEmbeddingPgvectorReadIntegrationTests(EmbeddingReadTestPostgresFactory factory)
    {
        _factory = factory;
        // Each test owns the store: the container is shared across the class (one per suite, gh#121), so start
        // every test from empty rather than accumulating rows across cases.
        ResetEmbeddingsAsync().GetAwaiter().GetResult();
    }

    // =================================================================================================================
    // Nearest-N by CosineDistance — the acceptance criteria's lead item. Basis-direction vectors make the expected
    // cosine distance exact (0, 0.2, 1) rather than approximate, so the ordering assertion is a hard equality, not a
    // "roughly sorted" hand-wave.
    // =================================================================================================================

    [Fact]
    public async Task NearestN_ShouldOrderByCosineDistanceAscending_AndRespectTake()
    {
        // Query points along dimension 0. Closest = identical direction (distance 0); middle = 0.8/0.6 blend
        // (distance exactly 0.2 by construction: cos_sim = dot / (|q||b|) = 0.8, both vectors unit norm); farthest
        // = orthogonal (distance 1). Seeded out of order so the assertion cannot pass by seed-order coincidence.
        await SeedAsync(
            Row(EmbeddingOwnerKind.SoftSignal, "farthest", Model, Direction((1, 1f))),
            Row(EmbeddingOwnerKind.SoftSignal, "closest", Model, Direction((0, 1f))),
            Row(EmbeddingOwnerKind.SoftSignal, "middle", Model, Direction((0, 0.8f), (1, 0.6f))));

        List<EmbeddingHit> top2 = await NearestAsync(EmbeddingOwnerKind.SoftSignal, Model, Direction((0, 1f)), take: 2);

        top2.Select(hit => hit.OwnerId).Should().Equal(
            ["closest", "middle"], "the two nearest by cosine distance, in ascending order — Take(2) excludes the farthest row entirely");
        top2[0].Distance.Should().BeApproximately(0.0, 1e-6, "an identical direction is cosine distance zero");
        top2[1].Distance.Should().BeApproximately(0.2, 1e-6, "the 0.8/0.6 unit blend is exactly 0.2 by construction");
    }

    [Fact]
    public async Task NearestN_ShouldReturnEmpty_WhenTheCorpusIsEmpty()
    {
        // No rows seeded for this (OwnerKind, Model) at all -- the query must return an empty result, not throw and
        // not fabricate a hit. This is the pass's cold-start / fully-caught-up state (NewsEmbeddingService's own
        // batch loop sees exactly this once every pending item is embedded).
        List<EmbeddingHit> hits = await NearestAsync(EmbeddingOwnerKind.SoftSignal, Model, Direction((0, 1f)), take: 5);

        hits.Should().BeEmpty("an empty corpus is a valid, unexceptional state for the similarity read");
    }

    [Fact]
    public async Task NearestN_ShouldReturnTheSoleRow_WhenTheCorpusHasExactlyOneRow()
    {
        await SeedAsync(Row(EmbeddingOwnerKind.SoftSignal, "only-row", Model, Direction((1, 1f))));

        List<EmbeddingHit> hits = await NearestAsync(EmbeddingOwnerKind.SoftSignal, Model, Direction((0, 1f)), take: 5);

        EmbeddingHit hit = hits.Should().ContainSingle("a one-row corpus has exactly one candidate, however far it is from the query").Which;
        hit.OwnerId.Should().Be("only-row");
        hit.Distance.Should().BeApproximately(1.0, 1e-6, "orthogonal to the query is cosine distance 1 -- the request still returns it, not a threshold-filtered empty set");
    }

    [Fact]
    public async Task NearestN_ShouldExcludeOtherOwnerKinds_EvenWhenTheirVectorPointsExactlyAtTheQuery()
    {
        // The Suggestion-kind row is the CLOSEST possible vector to the query (identical direction) -- if the
        // OwnerKind filter were dropped or wrong, it would rank #1 ahead of the true SoftSignal answer. Retrieval
        // filters by owner kind before ranking ("the nearest news item", never "the nearest anything" -- the
        // ConfigureEmbeddings doc comment's own words), so this is the filter-before-rank invariant, proven
        // adversarially rather than merely asserted.
        await SeedAsync(
            Row(EmbeddingOwnerKind.Suggestion, "wrong-kind-but-identical-vector", Model, Direction((0, 1f))),
            Row(EmbeddingOwnerKind.SoftSignal, "right-kind", Model, Direction((0, 0.8f), (1, 0.6f))));

        List<EmbeddingHit> hits = await NearestAsync(EmbeddingOwnerKind.SoftSignal, Model, Direction((0, 1f)), take: 5);

        hits.Select(hit => hit.OwnerId).Should().Equal(
            ["right-kind"], "a Suggestion-owned row must never surface from a SoftSignal query, however close its vector sits to the query");
    }

    [Fact]
    public async Task NearestN_ShouldExcludeOtherModels_EvenWhenTheirVectorPointsExactlyAtTheQuery()
    {
        // Same adversarial shape as the owner-kind case, over the OTHER half of the composite key: Model is part of
        // the key precisely because vectors from different models are not comparable (EmbeddingRecord's own doc
        // comment) -- so a stale row from a retired model must never outrank a same-model candidate, even when its
        // raw vector happens to point exactly at the query.
        await SeedAsync(
            Row(EmbeddingOwnerKind.SoftSignal, "wrong-model-but-identical-vector", OtherModel, Direction((0, 1f))),
            Row(EmbeddingOwnerKind.SoftSignal, "right-model", Model, Direction((0, 0.8f), (1, 0.6f))));

        List<EmbeddingHit> hits = await NearestAsync(EmbeddingOwnerKind.SoftSignal, Model, Direction((0, 1f)), take: 5);

        hits.Select(hit => hit.OwnerId).Should().Equal(
            ["right-model"], "a different model's vector must never outrank a same-model candidate, however close its raw direction sits to the query");
    }

    // =================================================================================================================
    // Stored-key normalization -- OwnerId for a SoftSignal (NewsRecord) is NewsDedupKey.For(url), computed by the
    // WRITER before the row ever reaches the database (gh#377). What only a real Postgres round-trip can prove is
    // that the composite key (OwnerKind, OwnerId, Model) behaves as the SAME idempotence guard here that it does on
    // NewsRecord.DedupKey itself: two differently-formatted URLs that canonicalize to one key collapse to one row.
    // =================================================================================================================

    [Fact]
    public async Task EmbeddingRecord_ShouldBeReadableByTheCanonicalDedupKey_RegardlessOfWhichRawUrlVariantProducedIt()
    {
        const string rawA = "https://WWW.Example.com/story/?utm_source=newsletter";
        const string rawB = "https://example.com/story";
        string canonical = NewsDedupKey.For(rawA);
        canonical.Should().Be(NewsDedupKey.For(rawB), "the two raw URLs are the same story once canonicalized -- the premise this test exercises");

        await SeedAsync(Row(EmbeddingOwnerKind.SoftSignal, canonical, Model, Direction((0, 1f))));

        // The lookup key is derived from rawB -- a DIFFERENTLY-FORMATTED URL than the one implicitly behind the
        // stored row -- proving the stored OwnerId is genuinely the normalized form, not merely whatever string the
        // writer happened to pass.
        EmbeddingRecord? found = await FindAsync(EmbeddingOwnerKind.SoftSignal, NewsDedupKey.For(rawB), Model);

        found.Should().NotBeNull("a lookup by the canonical key derived from ANY equivalent raw URL must find the stored row");
        found!.OwnerId.Should().Be(canonical, "the persisted OwnerId is the exact canonical string -- not re-normalized, not truncated, not case-folded further by storage");
    }

    [Fact]
    public async Task EmbeddingRecord_ShouldRefuseASecondRow_WhenTwoRawUrlVariantsNormalizeToTheSameOwnerId()
    {
        const string rawA = "https://example.com/second-story";
        const string rawB = "https://WWW.EXAMPLE.com/second-story/?fbclid=abc123";
        string canonicalA = NewsDedupKey.For(rawA);
        string canonicalB = NewsDedupKey.For(rawB);
        canonicalA.Should().Be(canonicalB, "the premise: two differently-formatted URLs for the same story canonicalize identically");

        await SeedAsync(Row(EmbeddingOwnerKind.SoftSignal, canonicalA, Model, Direction((0, 1f))));

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Embeddings.Add(Row(EmbeddingOwnerKind.SoftSignal, canonicalB, Model, Direction((1, 1f))));

        Func<Task> save = () => database.SaveChangesAsync();

        // The DB-enforced identity is (OwnerKind, OwnerId, Model) -- a second row under the SAME canonical key,
        // however different its vector, collides on PK_Embeddings. This is what makes re-embedding idempotent
        // (EmbeddingRecord's own doc comment): the row is meant to be UPDATED in place by the writer, never doubled.
        DbUpdateException thrown = (await save.Should().ThrowAsync<DbUpdateException>(
            "two rows keyed to the same canonical OwnerId must never coexist")).Which;
        PostgresException pg = thrown.InnerException.Should().BeOfType<PostgresException>().Which;
        pg.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("PK_Embeddings", "the refusal is the composite primary key, not an unrelated violation");
    }

    // =================================================================================================================
    // The migration this whole suite depends on: the vector extension and the HNSW cosine index the AddEmbeddingStore
    // migration builds (gh#109). If either were silently missing, every case above would still pass functionally
    // (Postgres falls back to a full scan for CosineDistance ordering) -- this case is the one that would actually
    // catch that regression, which is why the acceptance criteria name the HNSW index as its own scope item.
    // =================================================================================================================

    [Fact]
    public async Task Migration_ShouldHaveEnabledPgvector_AndBuiltTheHnswCosineIndex()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

        // Aliased "Value" because that is the column SqlQuery<T> projects a scalar from (the established
        // convention in this project -- see TradeOpeningFillKeyMigrationIntegrationTests.IndexCountAsync).
        List<string> extensions = await database.Database
            .SqlQuery<string>($"""SELECT extname AS "Value" FROM pg_extension WHERE extname = 'vector'""")
            .ToListAsync();
        extensions.Should().ContainSingle("the AddEmbeddingStore migration's CREATE EXTENSION IF NOT EXISTS vector must have run");

        List<string> indexDefs = await database.Database
            .SqlQuery<string>($"""SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname = 'IX_Embeddings_Vector_Cosine'""")
            .ToListAsync();
        string indexDef = indexDefs.Should().ContainSingle("the migration names this index explicitly -- its absence is a silent perf regression every other case here would miss").Which;
        indexDef.Should().Contain("hnsw", "HNSW was the deliberate choice over IVFFlat (builds usefully on an empty, growing table)");
        indexDef.Should().Contain("vector_cosine_ops", "cosine ops -- embedding vectors are direction-normalised, so L2 would rank partly on magnitude noise");
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    private sealed record EmbeddingHit(string OwnerId, double Distance);

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
        ContentHash = "gh855-test-content-hash",
        RecordedAt = _recordedAt,
    };

    private async Task SeedAsync(params EmbeddingRecord[] rows)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Embeddings.AddRange(rows);
        await database.SaveChangesAsync();
    }

    // The nearest-N read this card exists to prove (see the class remarks: gh#852 has not landed, so this is this
    // suite's own independent exercise of the query, not a call into production code).
    private async Task<List<EmbeddingHit>> NearestAsync(EmbeddingOwnerKind ownerKind, string model, Vector query, int take)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.Embeddings
            .Where(candidate => candidate.OwnerKind == ownerKind && candidate.Model == model)
            .OrderBy(candidate => candidate.Embedding.CosineDistance(query))
            .Take(take)
            .Select(candidate => new EmbeddingHit(candidate.OwnerId, candidate.Embedding.CosineDistance(query)))
            .ToListAsync();
    }

    private async Task<EmbeddingRecord?> FindAsync(EmbeddingOwnerKind ownerKind, string ownerId, string model)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.Embeddings.FirstOrDefaultAsync(
            candidate => candidate.OwnerKind == ownerKind && candidate.OwnerId == ownerId && candidate.Model == model);
    }

    private async Task ResetEmbeddingsAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Embeddings.ExecuteDeleteAsync();
    }
}
