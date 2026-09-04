using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;

namespace MarqSpec.TradingCopilot.IntegrationTests.Ai;

/// <summary>
/// Independent QA for <b>gh#1096</b> (paired with gh#1065, build PR #1095) — that each newly retrievable kind's
/// <b>partial</b> HNSW cosine index (<c>IX_Embeddings_Vector_Cosine_Suggestion</c> /
/// <c>IX_Embeddings_Vector_Cosine_JournalEntry</c>, added by the <c>AddContextVectorIndexes</c> migration) is
/// genuinely present and genuinely <i>chosen</i> for its kind's ranked read, so a crowd of closer other-kind rows
/// cannot starve <c>IEmbeddingRecall.NearestAsync</c> to zero.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the build PR could pin, and what it could not.</b> gh#1065's unit tier proves the shipping predicate
/// carries a <b>constant</b> owner kind (<c>PgVectorEmbeddingRecall.PredicateFor</c> compiled and asserted to hold a
/// <c>ConstantExpression</c>) — because a captured variable would become a SQL <i>parameter</i>, which no partial
/// index predicate can be proven to cover. That is necessary and not sufficient: a correct constant against a
/// <b>missing</b> index behaves identically to a parameter, and the symptom of either is a <i>silently empty</i>
/// result. Only a real, migrated Postgres holding enough rows to tip the planner can witness the difference, which
/// is what this suite is.
/// </para>
/// <para>
/// <b>The hazard, restated per kind.</b> gh#861 reproduced it for soft signals: pgvector's HNSW access method has no
/// vocabulary for a non-vector predicate, so an owner-kind filter is applied <i>after</i> the approximate scan has
/// already bounded its candidate window (about <c>hnsw.ef_search</c>, default 40). On a polymorphic table where
/// closer rows of other kinds fill that window first, the read returns <b>zero</b> neighbours though thousands
/// exist. Nothing throws and nothing logs — grounding simply, permanently, stops carrying that kind. gh#864 wrote
/// down the general rule ("any other owner kind needing a vector-ordered read wants its own partial index"), and
/// gh#1065 is the first increment to add kinds under it.
/// </para>
/// <para>
/// <b>Why one seed serves both kinds.</b> The noise crowd is deliberately built from the three owner kinds that have
/// <b>no</b> partial vector index of their own (<c>Topic</c> / <c>Rule</c> / <c>MarketSnapshot</c>), every vector of
/// which is closer to the query than every target row — so for the <c>Suggestion</c> read the crowd is 7,500 nearer
/// rows plus 3,750 equally-distant <c>JournalEntry</c> rows, and symmetrically for the <c>JournalEntry</c> read.
/// One 15,000-row seed therefore poses the starvation question independently to each new index, at the same
/// 25%-selectivity shape the sibling <see cref="NewsEmbeddingRecallIntegrationTests"/> established as the smallest
/// that reliably tips the planner. Keeping the noise off the indexed kinds also keeps the seed affordable: only the
/// 7,500 target rows are maintained incrementally into an HNSW index during the insert.
/// </para>
/// <para>
/// <b>Deliberately extreme separation, for a deterministic guard.</b> Every noise vector is constructed strictly
/// closer to the query than every target vector (see <see cref="Blend"/>), so a recall failure is <i>total</i>
/// (0 of n) rather than a fragile few-rows-short result that could flip on unrelated noise — the same discipline the
/// gh#861 suite adopted, and what makes this a regression guard rather than an occasionally-flaky one.
/// </para>
/// <para>
/// <b>The migration's own indexes are the ones under test.</b> Only the <i>table-wide</i>
/// <c>IX_Embeddings_Vector_Cosine</c> is dropped for the bulk seed and rebuilt after (pgvector's own bulk-load
/// guidance, and exactly what the gh#861 suite does) — the two partial indexes this suite exists to exercise are
/// never dropped or recreated by the fixture, so what serves the reads below is the DDL the migration shipped and
/// not a copy of it. The complementary claim — that those two indexes exist by name on the live migrated schema —
/// is carried in the same tier by <c>Data/InventoryIdentifierLiveSchemaIntegrationTests.cs</c>, which harvests the
/// names this suite's §2 inventory row states.
/// </para>
/// <para>
/// <b>Prove-red (gh#1096, recorded in the PR body).</b> Commenting the <c>Suggestion</c> index out of the
/// <c>AddContextVectorIndexes</c> migration reddens the suggestion case at zero neighbours recalled; the same for
/// the journal-entry index and its case; and each left the other case green, so neither passes on the other's
/// index. Restored afterwards — the migration is production code this tier does not edit.
/// </para>
/// </remarks>
public sealed class CrossKindEmbeddingRecallIntegrationTests : IClassFixture<EmbeddingReadTestPostgresFactory>
{
    // Must equal the host provider's Model. This suite drives the REAL production seam (IEmbeddingRecall, resolved
    // from DI below) and gh#889 scoped every ranked read to Model == provider.Model. EmbeddingReadTestPostgresFactory
    // wires no Cohere key, so the resolved provider is UnavailableEmbeddingProvider, whose Model is "none" -- seed
    // under any other model and that filter discards every row, collapsing this guard to zero for a reason that has
    // nothing to do with the partial indexes it exists to guard.
    private const string Model = "none";
    private const int Dimensions = TradingCopilotDbContext.EmbeddingDimensions;

    private const int TargetCountPerKind = 3_750;
    private const int NoiseCountPerKind = 2_500;
    private const int RequestedN = 5;

    // The kinds under test -- each has its own partial HNSW index from AddContextVectorIndexes (gh#1065).
    private static readonly (RetrievalKind Retrieval, EmbeddingOwnerKind Owner)[] _targets =
    [
        (RetrievalKind.Suggestion, EmbeddingOwnerKind.Suggestion),
        (RetrievalKind.JournalEntry, EmbeddingOwnerKind.JournalEntry),
    ];

    // The crowd: the three owner kinds with NO partial vector index, so the bulk seed maintains no ANN index for
    // them and their only route into a ranked read is the table-wide graph -- which is exactly the post-filter plan
    // the partial indexes must keep the targets out of.
    private static readonly EmbeddingOwnerKind[] _noiseKinds =
    [
        EmbeddingOwnerKind.Topic,
        EmbeddingOwnerKind.Rule,
        EmbeddingOwnerKind.MarketSnapshot,
    ];

    private static readonly DateTimeOffset _recordedAt = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly EmbeddingReadTestPostgresFactory _factory;

    public CrossKindEmbeddingRecallIntegrationTests(EmbeddingReadTestPostgresFactory factory)
    {
        _factory = factory;
        ResetEmbeddingsAsync().GetAwaiter().GetResult();
    }

    // =================================================================================================================
    // Scope bullet 4 — each new kind's partial HNSW index actually serves its read.
    //
    // One seed, two independent questions: with 7,500 closer rows of unindexed kinds crowding the approximate
    // candidate window (plus the OTHER target kind at the same distance band), does NearestAsync still return a full
    // n of the asked-for kind? Without that kind's partial index it returns zero -- the gh#861 shape, per kind.
    // =================================================================================================================

    [Fact]
    public async Task NearestAsync_ShouldReturnAFullNOfEachNewKind_WhenACrowdOfCloserOtherKindRowsWouldStarveIt()
    {
        Random random = new(1096); // fixed seed -- deterministic vectors, planner choice, and recall.
        float[] query = RandomUnit(random);

        List<EmbeddingRecord> rows = new((TargetCountPerKind * _targets.Length) + (NoiseCountPerKind * _noiseKinds.Length));

        // The crowd, blended only slightly away from the query -- i.e. deliberately closer to it than every target
        // row below. This is what fills the approximate candidate window before any owner-kind filter can run.
        foreach (EmbeddingOwnerKind kind in _noiseKinds)
        {
            for (int i = 0; i < NoiseCountPerKind; i++)
            {
                rows.Add(Row(kind, $"gh1096-noise-{kind}-{i}", Blend(query, RandomUnit(random), weightOther: 0.15)));
            }
        }

        // The answer sets: both new kinds, blended much farther from the query, so every one of them is farther
        // than every noise row. Both sit in the SAME distance band, so each kind's read also has to survive the
        // other kind's 3,750 rows -- a table-wide plan cannot tell them apart.
        foreach ((_, EmbeddingOwnerKind owner) in _targets)
        {
            for (int i = 0; i < TargetCountPerKind; i++)
            {
                rows.Add(Row(owner, $"gh1096-{owner}-{i}", Blend(query, RandomUnit(random), weightOther: 0.6)));
            }
        }

        // Bulk-load shape (pgvector's own guidance, and the sibling gh#861 suite's): the TABLE-WIDE index is dropped
        // for the insert and rebuilt after. The two PARTIAL indexes under test are never touched -- what serves the
        // reads below is the DDL AddContextVectorIndexes shipped.
        await DropTableWideVectorIndexAsync();
        await SeedAsync(rows);
        await RebuildTableWideVectorIndexAsync();
        // A freshly built index has no planner statistics until ANALYZE; running it explicitly makes the plan
        // choice -- and therefore this test's result -- deterministic rather than racing autovacuum's naptime.
        await AnalyzeAsync();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEmbeddingRecall recall = scope.ServiceProvider.GetRequiredService<IEmbeddingRecall>();

        foreach ((RetrievalKind retrieval, EmbeddingOwnerKind owner) in _targets)
        {
            IReadOnlyList<SemanticNeighbor> hits =
                await recall.NearestAsync(retrieval, query, RequestedN, CancellationToken.None);

            hits.Should().HaveCount(
                RequestedN,
                $"gh#1065's partial HNSW index for {owner} searches inside the owner kind the read filters on, so "
                + $"{NoiseCountPerKind * _noiseKinds.Length} closer rows of other kinds cannot starve it -- "
                + $"{TargetCountPerKind} {owner} rows exist. Zero here is the gh#861 starvation, reproduced per kind");

            // ...and every hit really is that kind. A count-only assertion would pass if a "fix" had widened the
            // search instead of scoping it, letting another owner kind's row through as a neighbour.
            IReadOnlyList<string> expected = [.. rows.Where(row => row.OwnerKind == owner).Select(row => row.OwnerId)];
            hits.Select(hit => hit.OwnerId).Should().BeSubsetOf(
                expected, $"the read is 'the nearest {owner}', never 'the nearest anything'");
        }
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    /// <summary>A uniformly-random unit vector — Box-Muller normal samples, then normalized.</summary>
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
    /// <param name="weightOther">How much of <paramref name="other"/> to blend in — larger pushes the result farther.</param>
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
        ContentHash = $"gh1096-{ownerId}",
        RecordedAt = _recordedAt,
    };

    private async Task SeedAsync(IReadOnlyList<EmbeddingRecord> rows)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Embeddings.AddRange(rows);
        await database.SaveChangesAsync();
    }

    private Task AnalyzeAsync() => ExecuteSqlAsync("""ANALYZE "Embeddings";""");

    // The same table-wide index AddEmbeddingStore builds (data dictionary §10) -- dropped and rebuilt around the seed
    // purely for suite speed. The two partial indexes this suite guards are deliberately left alone.
    private Task DropTableWideVectorIndexAsync() =>
        ExecuteSqlAsync("""DROP INDEX IF EXISTS "IX_Embeddings_Vector_Cosine";""");

    private Task RebuildTableWideVectorIndexAsync() => ExecuteSqlAsync(
        """CREATE INDEX "IX_Embeddings_Vector_Cosine" ON "Embeddings" USING hnsw ("Embedding" vector_cosine_ops);""");

    private async Task ExecuteSqlAsync(string sql)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task ResetEmbeddingsAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Embeddings.ExecuteDeleteAsync();
    }
}
