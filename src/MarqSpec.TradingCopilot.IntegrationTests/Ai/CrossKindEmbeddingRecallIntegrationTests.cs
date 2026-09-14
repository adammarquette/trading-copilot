using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
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
/// <b>Why one seed serves both kinds.</b> The noise crowd is built from the three owner kinds that have <b>no</b>
/// partial vector index of their own (<c>Topic</c> / <c>Rule</c> / <c>MarketSnapshot</c>) — a <i>seed-cost</i>
/// choice, not a correctness one, see below — every vector of
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
/// <b>The schema is asserted before the seed, from <c>pg_indexes</c> — and the load-bearing half is the
/// <i>targets</i>.</b> Every target kind must have a partial index, or this suite has nothing to prove chosen and
/// would report a starvation it was never in a position to prevent. That half is the guard.
/// </para>
/// <para>
/// <b>The noise half is cost and honesty, not protection — measured, not assumed (gh#1112).</b> It is tempting to
/// argue that an indexed noise kind "is searched inside its own graph and stops crowding", and this suite's first
/// version said exactly that. <b>It is false, and was disproved by running it:</b> with <c>SoftSignal</c> (which
/// <i>does</i> hold <c>IX_Embeddings_Vector_Cosine_SoftSignal</c>) substituted into the crowd and the
/// <c>Suggestion</c> index dropped, the suggestion read still starved to <b>0 of 5</b>. The crowding happens in the
/// <i>table-wide</i> <c>IX_Embeddings_Vector_Cosine</c>, which holds every row of every kind — a noise row having a
/// second home in some other partial index changes nothing about the window it fills. So the noise assertion buys
/// two real but smaller things: the bulk seed never pays incremental HNSW maintenance for 7,500 rows it will never
/// query, and the <c>_noiseKinds</c> list cannot quietly stop meaning what the remarks say it means — the drift that
/// hit the sibling gh#861 suite when gh#1065 gave <c>Suggestion</c> an index (gh#1110), which for the same reason
/// was a cost and comment defect there too, never a hole in its guard.
/// </para>
/// <para>
/// <b>Prove-red (gh#1096, PR #1108; the later breaks gh#1112, PR #1113).</b> The <i>behavioural</i> proof came first, before the
/// pre-seed schema guard existed: commenting the <c>Suggestion</c> index out of the <c>AddContextVectorIndexes</c>
/// migration returned <b>0 of 5</b> suggestion neighbours, the journal-entry index the same for its kind, and each
/// left the other kind green — so neither passes on the other's index. (Both kinds are recalled before either is
/// asserted, and their counts asserted as one map, so that claim is read off a single failure rather than inferred
/// past a fail-fast loop that would have left the second kind <i>unevaluated</i> — which is not the same as green.)
/// With the schema guard in place the same
/// break now reddens <i>earlier</i>, at the closed-set assertion, which is a sharper diagnosis of the same defect;
/// that behavioural evidence is why the index matters and is kept on record rather than superseded. The guard
/// itself was proven able to fail three ways: smuggling the indexed <c>SoftSignal</c> kind into the crowd, dropping
/// a target index, and adding a set-predicate index (<c>WHERE "OwnerKind" = ANY (ARRAY[3, 5])</c>) over two noise
/// kinds — the last being the false negative a substring match would have let through. One further run was made to
/// settle the noise-kind question rather than argue it (gh#1112): an <i>indexed</i> kind substituted into the crowd,
/// with the target index dropped, still starved the read to 0 of 5 — which is why the paragraph above calls the
/// noise half cost rather than protection. Restored afterwards — the migration is production code this tier does not
/// edit.
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

    // The one predicate shape a partial index on this table is allowed to use. Anchored on the quoted column name
    // and a whole integer, so a two-digit owner kind can never be read as a one-digit one.
    private static readonly Regex _ownerKindEquality = new(@"""OwnerKind"" = ([0-9]+)\)", RegexOptions.Compiled);

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

        // THE SCHEMA, BEFORE THE SEED -- read off the LIVE migrated database rather than trusted to a comment.
        //
        // The load-bearing half is the TARGETS: a target kind with no partial index of its own gives this suite
        // nothing to prove chosen, and it would report a starvation it was never in a position to prevent.
        //
        // The noise half is seed cost and accuracy, NOT protection. An earlier version of this comment said an
        // indexed noise kind "is searched inside its own graph and stops crowding" -- that is false, and gh#1112
        // settled it by running it: SoftSignal, which does hold IX_Embeddings_Vector_Cosine_SoftSignal, substituted
        // into the crowd with the target index dropped still starved the read to 0 of 5. The crowding happens in the
        // TABLE-WIDE IX_Embeddings_Vector_Cosine, which holds every row of every kind, so a second home in another
        // partial index changes nothing about the window a noise row fills. What the noise half buys is a bulk seed
        // that never pays HNSW maintenance for rows this suite will not query, and a _noiseKinds list that cannot
        // quietly stop meaning what the remarks say -- the drift that hit the sibling gh#861 suite when gh#1065 gave
        // Suggestion an index (gh#1110, since fixed there; that suite now names Topic / Rule / MarketSnapshot).
        //
        // The assertion is CLOSED and reads integers, not substrings: the set of owner kinds any partial index on
        // this table selects must be exactly {SoftSignal, Suggestion, JournalEntry}. A new partial index on any
        // other kind -- whatever access method, hnsw or ivfflat -- changes that set and reddens here, and a
        // two-digit owner kind can never be confused with a one-digit one the way a `Contains("= 1")` would.
        IReadOnlyList<string> indexDefinitions = await IndexDefinitionsAsync();
        IReadOnlyList<EmbeddingOwnerKind> partitionedKinds = PartialIndexOwnerKinds(indexDefinitions);

        EmbeddingOwnerKind[] mayBePartitioned =
            [EmbeddingOwnerKind.SoftSignal, .. _targets.Select(target => target.Owner)];
        partitionedKinds.Should().BeEquivalentTo(
            mayBePartitioned,
            "exactly the soft-signal index gh#864 added and the two AddContextVectorIndexes added (gh#1065) may "
            + "partition this table. The load-bearing half is the TARGETS: without a partial index of its own a "
            + "target kind's read has nothing for this suite to prove chosen. The noise half is seed cost and "
            + "honesty, not protection -- an indexed noise row still crowds the table-wide graph, measured (gh#1112). "
            + "If you are reading this because a "
            + "LEGITIMATE new partial index reddened it: that is the guard working. Decide which side the new kind is "
            + "on -- add it to _targets (and give it a case) if its ranked read is under test, or leave it out of "
            + "_noiseKinds and add it here if it is neither. Do not widen this to a subset check: the closed set is "
            + "what makes an index appearing on a NOISE kind impossible to miss");

        foreach (EmbeddingOwnerKind kind in _noiseKinds)
        {
            partitionedKinds.Should().NotContain(
                kind,
                $"{kind} is a NOISE kind. An index of its own would NOT stop it crowding -- that was measured and "
                + "is false (gh#1112) -- but it would make the bulk seed pay HNSW maintenance for rows this suite "
                + "never queries, and it would leave _noiseKinds meaning something the remarks do not say");
        }

        // The crowd, blended only slightly away from the query -- i.e. deliberately closer to it than every target
        // row below. This is what fills the approximate candidate window before any owner-kind filter can run.
        int noiseSeeded = 0; // sanity, not a guard -- it mirrors the gh#861 sibling's own seed tally.
        foreach (EmbeddingOwnerKind kind in _noiseKinds)
        {
            for (int i = 0; i < NoiseCountPerKind; i++)
            {
                rows.Add(Row(kind, $"gh1096-noise-{kind}-{i}", Blend(query, RandomUnit(random), weightOther: 0.15)));
                noiseSeeded++;
            }
        }

        noiseSeeded.Should().Be(
            NoiseCountPerKind * _noiseKinds.Length, "sanity: the whole crowd was built, mirroring the gh#861 sibling");

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

        // BOTH kinds are read BEFORE anything is asserted, and their counts are asserted TOGETHER as one map. A
        // per-kind assertion inside the loop is fail-fast: the first kind's failure would abandon the run with the
        // second kind never evaluated, which is not the same as the second kind passing -- and "each break left the
        // OTHER kind green" is precisely the claim this suite's prove-red record makes. Asserting the map makes a
        // run report every kind's recall, so that claim is readable off a single failure rather than inferred.
        Dictionary<EmbeddingOwnerKind, IReadOnlyList<SemanticNeighbor>> hitsByKind = [];
        foreach ((RetrievalKind retrieval, EmbeddingOwnerKind owner) in _targets)
        {
            hitsByKind[owner] = await recall.NearestAsync(retrieval, query, RequestedN, CancellationToken.None);
        }

        hitsByKind.ToDictionary(entry => entry.Key, entry => entry.Value.Count).Should().BeEquivalentTo(
            _targets.ToDictionary(target => target.Owner, _ => RequestedN),
            "gh#1065's partial HNSW index for each kind searches inside the owner kind that kind's read filters on, "
            + $"so the {NoiseCountPerKind * _noiseKinds.Length} closer rows of other kinds cannot starve it -- "
            + $"{TargetCountPerKind} rows of each target kind exist. A zero here is the gh#861 starvation, "
            + "reproduced for whichever kind reports it");

        // ...and every hit really is its own kind. A count-only assertion would pass if a "fix" had widened the
        // search instead of scoping it, letting another owner kind's row through as a neighbour. Gathered across
        // every kind and asserted once, for the same reason the counts are: a per-kind assertion inside the loop
        // would abandon the run at the first offender, and "the other kind was clean" is then an inference about an
        // iteration that never ran rather than an observation.
        List<string> foreignHits = [];
        foreach ((EmbeddingOwnerKind owner, IReadOnlyList<SemanticNeighbor> hits) in hitsByKind)
        {
            HashSet<string> ofThisKind =
                [.. rows.Where(row => row.OwnerKind == owner).Select(row => row.OwnerId)];
            foreignHits.AddRange(hits
                .Where(hit => !ofThisKind.Contains(hit.OwnerId))
                .Select(hit => $"{owner} recalled {hit.OwnerId}"));
        }

        // The offenders ride the message, not just "at least one item": the whole reason they are gathered rather
        // than asserted per kind is so one failure names EVERY kind that leaked, and a message that reports only
        // the first would give that back.
        foreignHits.Should().BeEmpty(
            "each read is 'the nearest row OF ITS KIND', never 'the nearest anything' -- a widened search would "
            + "satisfy the counts above while returning another owner kind's rows. Foreign hits: {0}",
            string.Join("; ", foreignHits));
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

    /// <summary>
    /// The owner kinds selected by a <b>partial</b> index on the <c>Embeddings</c> table, parsed out of the live
    /// index definitions.
    /// </summary>
    /// <remarks>
    /// Two things are asserted on the way, so an exotic predicate cannot slip past as "no owner kind": every partial
    /// index on this table must partition on <c>OwnerKind</c> at all, and must do so by simple equality — a set
    /// predicate (<c>= ANY (ARRAY[…])</c>) or a range would index several kinds behind a shape this parse does not
    /// read, which is exactly the false negative that would let an indexed noise kind through.
    /// </remarks>
    /// <param name="definitions">Every index definition on the table.</param>
    /// <returns>The distinct owner kinds any partial index selects.</returns>
    private static IReadOnlyList<EmbeddingOwnerKind> PartialIndexOwnerKinds(IReadOnlyList<string> definitions)
    {
        List<EmbeddingOwnerKind> kinds = [];
        foreach (string definition in definitions)
        {
            int where = definition.IndexOf(" WHERE ", StringComparison.Ordinal);
            if (where < 0)
            {
                continue; // a table-wide index partitions nothing
            }

            Match match = _ownerKindEquality.Match(definition, where);
            match.Success.Should().BeTrue(
                $"every partial index on Embeddings must partition by a simple OwnerKind equality, or this parse "
                + $"reports 'no kind' for an index that really does cover one -- saw: {definition}");

            EmbeddingOwnerKind kind = (EmbeddingOwnerKind)int.Parse(
                match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!kinds.Contains(kind))
            {
                kinds.Add(kind);
            }
        }

        return kinds;
    }

    /// <summary>
    /// Every index definition on the live, migrated <c>Embeddings</c> table, read from <c>pg_indexes</c> — the only
    /// authority true by construction for "which owner kinds have an index of their own", since a grep of the
    /// append-only migrations would still find a dropped one. <b>Unfiltered by access method</b>: an
    /// <c>ivfflat</c> index on a noise kind would spoil the crowd exactly as an <c>hnsw</c> one would.
    /// </summary>
    private async Task<IReadOnlyList<string>> IndexDefinitionsAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        DbConnection connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            """SELECT indexdef FROM pg_indexes WHERE tablename = 'Embeddings';""";

        List<string> definitions = [];
        await using (DbDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                definitions.Add(reader.GetString(0));
            }
        }

        return definitions;
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
