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
/// <c>Embeddings</c> table — SoftSignal shares one table with Suggestion / Rule / MarketSnapshot per
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
/// <b>gh#864 (filed alongside this suite):</b> the observed defect is pinned per the QA guard discipline —
/// <c>PgVectorNewsSimilarity.NearestNewsAsync</c> is not touched here (QA does not edit <c>src/**</c> outside
/// this project); the fix is handed to the Coding Agent as a choice among three options (raise
/// <c>hnsw.ef_search</c> for this query, add a covering/partial index on <c>OwnerKind</c>, or accept and document
/// the approximation) named in that issue.
/// </para>
/// <para>
/// <b>gh#858 blocks this suite in CI — confirmed on this PR's own pre-merge run, not assumed.</b> CI's real
/// Testcontainers Postgres (a genuinely fresh container, migrated in-process — the same window
/// <see cref="NewsEmbeddingPgvectorReadIntegrationTests"/> hit) throws on <see cref="SeedAsync"/>'s bulk
/// <c>Vector</c> write with the identical <c>NotSupportedException: Cannot resolve 'vector' to a fully
/// qualified datatype name</c> that blocks 7 of that sibling suite's 8 cases. The local native-Postgres
/// investigation described above sidestepped gh#858 by construction (its <c>vector</c> extension was created by
/// a separate <c>psql</c> process before this harness's own connection ever opened, so its type cache was never
/// stale) — which is exactly why it could reach and prove the recall question, but is not what CI's from-scratch
/// container run does. The case below is therefore <c>Skip</c>ped citing gh#858 rather than left red for an
/// unrelated reason; once gh#858 lands, un-skip it — it goes on to prove (or disprove) the recall hazard for
/// real, in CI, and becomes gh#864's regression guard the same run.
/// </para>
/// </remarks>
public sealed class NewsEmbeddingRecallIntegrationTests : IClassFixture<EmbeddingReadTestPostgresFactory>
{
    private const string Model = "cohere-embed-v3-recall-test";
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
        EmbeddingOwnerKind.Suggestion,
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
    // DEFECT gh#864: a mixed-owner-kind Embeddings table can starve NearestNewsAsync's SoftSignal recall down to
    // ZERO, even though thousands of SoftSignal rows exist -- the HNSW approximate scan's candidate window fills
    // with other-owner-kind rows (deliberately seeded closer to the query than every SoftSignal row) before the
    // OwnerKind filter is applied. This pins the OBSERVED (broken) behaviour per the QA guard discipline; the fix
    // is gh#864's to choose, and this assertion becomes its regression guard once it lands.
    //
    // Skipped citing gh#858 (a SEPARATE, already-filed defect this PR did not introduce and does not fix): CI's
    // real Testcontainers run hits it on SeedAsync's bulk Vector write, before this case ever reaches the
    // NearestNewsAsync call it exists to prove -- see the class remarks for the confirmed CI failure and why the
    // local investigation that found the gh#864 hazard did not hit it. Un-skip once gh#858 lands.
    // =================================================================================================================

    [Fact(Skip = "blocked by gh#858 -- SeedAsync's bulk Vector write hits the stale post-migrate type cache on CI's real Testcontainers run; see class remarks")]
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
        INewsEmbeddingSimilarity similarity = scope.ServiceProvider.GetRequiredService<INewsEmbeddingSimilarity>();

        IReadOnlyList<SemanticNeighbor> hits = await similarity.NearestNewsAsync(query, RequestedN, CancellationToken.None);

        noiseIndex.Should().Be(NoiseCountPerKind * _noiseKinds.Length, "sanity: every noise row was seeded");

        // DEFECT gh#864: NearestNewsAsync returns ZERO SoftSignal neighbours here, though SoftSignalCount (3,750)
        // exist -- the HNSW approximate scan's candidate window (bounded by the default hnsw.ef_search = 40)
        // fills entirely with closer non-SoftSignal rows before the OwnerKind filter can accept a SoftSignal
        // candidate. This assertion pins that OBSERVED behaviour; once gh#864's fix lands (raised ef_search for
        // this query, a covering/partial OwnerKind index, or a documented accepted-approximation with a
        // narrower guard), it should be revisited -- a fix should flip this to
        // `hits.Should().HaveCount(RequestedN)` and this becomes its regression guard.
        hits.Should().BeEmpty(
            "DEFECT gh#864: the mixed-owner-kind approximate scan starves SoftSignal recall to zero even though "
            + $"{SoftSignalCount} SoftSignal rows exist -- see class remarks and gh#864 for the fix decision");
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
