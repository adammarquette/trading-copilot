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
/// Independent QA for gh#861 (sub-issue of gh#763, epic gh#14 [D2]) — the recall hazard the gh#852 adversarial
/// review flagged in <see cref="PgVectorNewsSimilarity"/>: <c>.Where(OwnerKind == SoftSignal)</c> filters the
/// HNSW <b>approximate</b> nearest-neighbour scan, and pgvector applies that filter <i>after</i> the scan, not as
/// part of it (the HNSW access method has no vocabulary for a non-vector predicate). On the polymorphic
/// <c>Embeddings</c> table — SoftSignal shares one table with Topic / Rule / MarketSnapshot per
/// <see cref="EmbeddingOwnerKind"/> — the query can under-return: fewer than <c>n</c> SoftSignal neighbours even
/// when <c>n</c> or more exist, because the approximate candidate window fills with other owner kinds first.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is real, not merely theoretical — reproduced against the production method.</b> Local investigation
/// (a native Postgres 16 + pgvector 0.6.0 instance, and the actual <c>MigrateAsync</c>-built schema driven
/// through the real <see cref="PgVectorNewsSimilarity"/> class — see the PR body for the full account) found
/// that Postgres's cost-based planner does not always avoid the hazardous plan just because a covering
/// <c>OwnerKind</c> index exists (the <c>PK_Embeddings</c> / <c>IX_Embeddings_OwnerKind_RecordedAt</c> composite
/// indexes, both leading with <c>OwnerKind</c>): once the number of SoftSignal rows themselves grows large enough
/// that scanning-and-sorting them exceeds the (largely table-size-independent) cost of an HNSW graph descent, the
/// planner switches to the HNSW-plus-post-filter plan — and when the crowd is close enough to the query, that
/// plan returns <b>zero</b> SoftSignal neighbours even though thousands exist. The seed below (25% SoftSignal
/// selectivity at 15,000 total rows) is the smallest configuration found that reliably reproduces the flip in
/// this environment; smaller tables kept the safe, exact plan.
/// </para>
/// <para>
/// <b>Deliberately extreme separation, for a deterministic guard.</b> Every "noise" (non-SoftSignal) vector is
/// constructed strictly closer to the query than every SoftSignal vector (see <see cref="Blend"/>), so recall
/// failure — when it happens — is total (0 of <c>n</c>) rather than a fragile few-rows-short result that could
/// flip pass/fail on unrelated noise. This is what makes the assertion below a reliable regression guard rather
/// than an occasionally-flaky one.
/// </para>
/// <para>
/// <b>The noise crowd names owner kinds with no partial vector index of their own (gh#1110) — for cost and
/// accuracy, not because the guard needs it (corrected in gh#1112).</b> gh#1065 gave <c>Suggestion</c>
/// <c>IX_Embeddings_Vector_Cosine_Suggestion</c>, so it was swapped out for <c>Topic</c>; currently Topic, Rule and
/// MarketSnapshot are unindexed (see <see cref="EmbeddingOwnerKind"/> and the AddContextVectorIndexes-family
/// migrations in <c>src/MarqSpec.TradingCopilot.Data/Migrations</c>).
/// </para>
/// <para>
/// <b>What that swap does <i>not</i> mean.</b> The remark written alongside the swap reasoned that an indexed noise
/// kind "would not
/// crowd the SoftSignal-only index, so it could no longer starve recall and would miss the hazard". That is not how
/// the crowding works, and running it settled the point: the red path here is served by the <i>table-wide</i>
/// <c>IX_Embeddings_Vector_Cosine</c>, which holds every row of every owner kind, so a noise row having a second
/// home in some other partial index fills the candidate window exactly as an unindexed one does — measured in
/// gh#1112 on the sibling <c>CrossKindEmbeddingRecallIntegrationTests</c>, where substituting an <i>indexed</i>
/// kind into the crowd and dropping the target index still starved the read to 0 of 5. gh#1110's own issue body had
/// it right — it says in as many words that the guard is unaffected — so this is a remark that overtook its card,
/// not a card that was wrong. Nothing indexes
/// <c>SoftSignal</c>'s partial graph but SoftSignal rows, so nothing was ever in a position to crowd it. What the
/// swap buys is a bulk seed that no longer pays incremental HNSW maintenance for 3,750 rows it never queries, and a
/// noise list that still means what this remark says it means.
/// </para>
/// <para>
/// <b>gh#864 (filed alongside this suite):</b> the observed defect is pinned per the QA guard discipline —
/// <c>PgVectorNewsSimilarity.NearestNewsAsync</c> is not touched here (QA does not edit <c>src/**</c> outside
/// this project); the fix is handed to the Coding Agent as a choice among three options (raise
/// <c>hnsw.ef_search</c> for this query, add a covering/partial index on <c>OwnerKind</c>, or accept and document
/// the approximation) named in that issue.
/// </para>
/// <para>
/// <b>gh#858 — hit on this PR's first pre-merge run, then landed mid-PR.</b> CI's real Testcontainers Postgres (a
/// genuinely fresh container, migrated in-process) initially threw on <see cref="SeedAsync"/>'s bulk
/// <c>Vector</c> write with the same <c>NotSupportedException: Cannot resolve 'vector' to a fully qualified
/// datatype name</c> that blocked 7 of <see cref="NewsEmbeddingPgvectorReadIntegrationTests"/>'s 8 cases — the
/// local native-Postgres investigation described above sidestepped it by construction (its <c>vector</c>
/// extension was created by a separate <c>psql</c> process before this harness's own connection ever opened, so
/// its type cache was never stale), which is exactly why it could reach and prove the recall question before CI
/// could. gh#858 landed (<c>StartupTasks</c> now reloads the type catalog post-migration) while this PR was
/// open; merging that fix in un-blocked this case for real, in CI, on the real Testcontainers image — it is no
/// longer <c>Skip</c>ped.
/// </para>
/// </remarks>
public sealed class NewsEmbeddingRecallIntegrationTests : IClassFixture<EmbeddingReadTestPostgresFactory>
{
    // Must equal the host provider's Model. This suite drives the REAL production seam (NearestNewsAsync, resolved
    // from DI below), and gh#889 scoped that read to Model == provider.Model. EmbeddingReadTestPostgresFactory wires
    // no Cohere key, so the resolved provider is UnavailableEmbeddingProvider, whose Model is "none" -- seed under
    // any other model and gh#889's filter discards every SoftSignal row, collapsing this gh#864 recall guard to zero
    // for a reason unrelated to the partial index it exists to guard.
    private const string Model = "none";
    private const int Dimensions = TradingCopilotDbContext.EmbeddingDimensions;

    // 25% SoftSignal selectivity at 15,000 total rows -- the smallest configuration this suite's investigation
    // found that reliably tips Postgres's planner from the safe, exact OwnerKind-index plan to the approximate
    // HNSW-plus-post-filter plan (see class remarks). The three "other" owner kinds share the remaining 75%
    // evenly, matching the polymorphic table the hazard describes.
    private const int SoftSignalCount = 3_750;
    private const int NoiseCountPerKind = 3_750;
    private const int RequestedN = 5;

    private static readonly EmbeddingOwnerKind[] _noiseKinds =
    [
        EmbeddingOwnerKind.Topic,
        EmbeddingOwnerKind.Rule,
        EmbeddingOwnerKind.MarketSnapshot,
    ];

    private static readonly DateTimeOffset _recordedAt = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly EmbeddingReadTestPostgresFactory _factory;

    public NewsEmbeddingRecallIntegrationTests(EmbeddingReadTestPostgresFactory factory)
    {
        _factory = factory;
        // Each test owns the store: the container is shared across the class (one per suite, gh#121), so start
        // from empty rather than accumulating rows across cases. This suite carries a single case, but the reset
        // keeps it independent of run order / fixture reuse the way the sibling gh#855 suite does.
        ResetEmbeddingsAsync().GetAwaiter().GetResult();
    }

    // =================================================================================================================
    // REGRESSION GUARD for gh#864 (was a pinned defect until the fix landed). A mixed-owner-kind Embeddings table
    // used to starve NearestNewsAsync's SoftSignal recall down to ZERO, even though thousands of SoftSignal rows
    // existed -- the HNSW approximate scan's candidate window filled with other-owner-kind rows (deliberately
    // seeded closer to the query than every SoftSignal row) before the OwnerKind filter was applied.
    //
    // The fix is a PARTIAL HNSW index over the soft-signal rows only (AddSoftSignalVectorIndex), so the vector
    // search happens inside the owner kind the read filters on and there is nothing left for a post-filter to
    // discard. Dropping that index -- or reverting to the table-wide one -- reddens this case, which is the whole
    // point of leaving the seeding deliberately adversarial.
    // =================================================================================================================

    [Fact]
    public async Task NearestNewsAsync_ShouldReturnNSoftSignalNeighbours_WhenTheEmbeddingsTableIsMixedOwnerKindAndAtLeastNExist()
    {
        Random random = new(861); // fixed seed -- deterministic vectors, deterministic planner choice, deterministic recall.
        float[] query = RandomUnit(random);

        List<EmbeddingRecord> rows = new(SoftSignalCount + (NoiseCountPerKind * _noiseKinds.Length));

        // "Noise": the other three owner kinds sharing the polymorphic table, each vector blended only slightly
        // away from the query -- i.e. deliberately closer to it than any SoftSignal row below. This is what
        // crowds the approximate candidate window before the OwnerKind filter ever runs.
        int noiseIndex = 0;
        foreach (EmbeddingOwnerKind kind in _noiseKinds)
        {
            for (int i = 0; i < NoiseCountPerKind; i++)
            {
                float[] vector = Blend(query, RandomUnit(random), weightOther: 0.15);
                rows.Add(Row(kind, $"noise-{kind}-{i}", vector));
                noiseIndex++;
            }
        }

        // SoftSignal: the answer set the query actually wants -- blended much farther from the query, so every
        // one of them is farther away than every noise row above (the "existing answers get crowded out" shape
        // the gh#852 review described).
        for (int i = 0; i < SoftSignalCount; i++)
        {
            float[] vector = Blend(query, RandomUnit(random), weightOther: 0.6);
            rows.Add(Row(EmbeddingOwnerKind.SoftSignal, $"soft-{i}", vector));
        }

        // Maintaining the HNSW index incrementally over 15,000 individual inserts is minutes slower than a bulk
        // rebuild once the rows are in -- so the index the AddEmbeddingStore migration created is dropped for the
        // seed and rebuilt after, exactly as pgvector's own bulk-load guidance recommends. This is test-seeding
        // infrastructure only; the query under test still runs against the real, fully-built index below.
        await DropVectorIndexAsync();
        await SeedAsync(rows);
        await RebuildVectorIndexAsync();
        // A fresh index has no planner statistics until ANALYZE (autovacuum) runs; running it explicitly makes
        // the planner's plan choice -- and therefore this test's result -- deterministic rather than racing
        // autovacuum's default one-minute naptime.
        await AnalyzeAsync();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        // gh#1065 moved the RANKED read onto the kind-parameterised IEmbeddingRecall seam; same production read.
        IEmbeddingRecall recall = scope.ServiceProvider.GetRequiredService<IEmbeddingRecall>();

        IReadOnlyList<SemanticNeighbor> hits =
            await recall.NearestAsync(RetrievalKind.News, query, RequestedN, CancellationToken.None);

        noiseIndex.Should().Be(NoiseCountPerKind * _noiseKinds.Length, "sanity: every noise row was seeded");

        // gh#864's regression guard. Before the partial index, this returned ZERO SoftSignal neighbours though
        // SoftSignalCount (3,750) existed: the approximate scan's candidate window (bounded by the default
        // hnsw.ef_search = 40) filled entirely with closer non-SoftSignal rows before the OwnerKind filter could
        // accept a SoftSignal candidate. The partial index removes the post-filter entirely, so a full n comes back.
        hits.Should().HaveCount(
            RequestedN,
            "gh#864: the soft-signal partial HNSW index searches inside the owner kind this read filters on, so a "
            + $"crowd of closer other-kind rows cannot starve recall -- {SoftSignalCount} SoftSignal rows exist");

        // ...and every one of them is genuinely a soft signal. Counting alone would pass if the fix had widened
        // the search instead of scoping it, letting another owner kind's row through as a "neighbour".
        IReadOnlyList<string> softSignalIds = [.. rows
            .Where(row => row.OwnerKind == EmbeddingOwnerKind.SoftSignal)
            .Select(row => row.OwnerId)];
        hits.Select(hit => hit.OwnerId).Should().BeSubsetOf(
            softSignalIds, "the read is 'the nearest NEWS item', never 'the nearest anything'");
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    /// <summary>A uniformly-random unit vector -- Box-Muller normal samples, then normalized.</summary>
    private static float[] RandomUnit(Random random)
    {
        float[] v = new float[Dimensions];
        for (int i = 0; i < v.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble(); // (0, 1], avoids Log(0)
            double u2 = random.NextDouble();
            v[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return Normalize(v);
    }

    /// <summary>Blends <paramref name="anchor"/> with <paramref name="other"/> and re-normalizes to a unit vector.</summary>
    /// <param name="anchor">The query direction every seeded row is blended toward or away from.</param>
    /// <param name="other">A fresh random unit vector supplying the "away from anchor" component.</param>
    /// <param name="weightOther">
    /// How much of <paramref name="other"/> to blend in -- smaller keeps the result closer (in cosine terms) to
    /// <paramref name="anchor"/>; larger pushes it farther away.
    /// </param>
    private static float[] Blend(float[] anchor, float[] other, double weightOther)
    {
        float[] v = new float[anchor.Length];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = (float)(((1 - weightOther) * anchor[i]) + (weightOther * other[i]));
        }

        return Normalize(v);
    }

    private static float[] Normalize(float[] v)
    {
        double normSquared = 0;
        foreach (float x in v)
        {
            normSquared += (double)x * x;
        }

        double norm = Math.Sqrt(normSquared);
        float[] result = new float[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            result[i] = (float)(v[i] / norm);
        }

        return result;
    }

    private static EmbeddingRecord Row(EmbeddingOwnerKind ownerKind, string ownerId, float[] embedding) => new()
    {
        OwnerKind = ownerKind,
        OwnerId = ownerId,
        Model = Model,
        Dimensions = Dimensions,
        Embedding = new Vector(embedding),
        ContentHash = $"gh861-{ownerId}",
        RecordedAt = _recordedAt,
    };

    private async Task SeedAsync(IReadOnlyList<EmbeddingRecord> rows)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Embeddings.AddRange(rows);
        await database.SaveChangesAsync();
    }

    private async Task AnalyzeAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Database.ExecuteSqlRawAsync("""ANALYZE "Embeddings";""");
    }

    // Same index the AddEmbeddingStore migration builds (data dictionary §10) -- dropped and rebuilt around the
    // seed purely for suite speed (see the call site's remarks); the query under test runs against this exact
    // definition, fully built, either way.
    private async Task DropVectorIndexAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Database.ExecuteSqlRawAsync("""DROP INDEX IF EXISTS "IX_Embeddings_Vector_Cosine";""");
    }

    private async Task RebuildVectorIndexAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Database.ExecuteSqlRawAsync(
            """CREATE INDEX "IX_Embeddings_Vector_Cosine" ON "Embeddings" USING hnsw ("Embedding" vector_cosine_ops);""");
    }

    private async Task ResetEmbeddingsAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Embeddings.ExecuteDeleteAsync();
    }
}
