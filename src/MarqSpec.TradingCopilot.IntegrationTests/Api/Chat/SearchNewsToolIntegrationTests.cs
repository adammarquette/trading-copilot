using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Signals;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api.Chat;

/// <summary>
/// Independent QA for gh#988 (of gh#987, R-6) — the <c>search_news</c> chat tool's <b>pgvector recall</b>, driven
/// through the <b>real tool</b> (<see cref="SearchNewsTool"/>, resolved from DI) over the shared
/// <see cref="MarqSpec.TradingCopilot.Api.Ai.NewsRetrievalService"/> pipeline, against real Postgres + pgvector.
/// Written from the gh#988 issue body, independently of the pipeline's own implementation: the tool's
/// degrade / ledger-fail-open / arg-parse logic is already unit-covered with fakes
/// (<c>SearchNewsToolTests</c>); what only real Postgres proves is the first-stage recall itself — real vectors,
/// the real <c>IX_Embeddings_Vector_Cosine_SoftSignal</c> partial index the query must stay matchable by (gh#864),
/// and the real hydrate + rerank-consumption steps downstream of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope versus the sibling suites.</b> <c>NewsEmbeddingRecallIntegrationTests</c> (gh#861) already proves the
/// HNSW-starvation hazard at 15,000 rows directly against <c>INewsEmbeddingSimilarity</c>; that is not repeated
/// here. <c>NewsGroundingIntegrationTests</c> (gh#996, PR #1033) already proves the pipeline end-to-end through the
/// <b>chat turn</b> endpoint, with the endpoint's own fail-open <c>catch</c> around retrieval. This suite is the
/// layer between them: the <c>search_news</c> <b>tool</b> itself — its JSON in/out, its own fail-<b>closed</b>
/// <c>catch</c> (distinct from the endpoint's fail-open one), and whether the pipeline's returned list order is
/// genuinely the reranker's, not a silently-substituted recall order.
/// </para>
/// <para>
/// <b>The doubled seam</b> is <see cref="AdversarialEmbeddingProvider"/> only (Cohere cannot exist pre-merge — no
/// key, no egress); <c>PgVectorNewsSimilarity</c>'s real cosine-distance read (including its <c>OwnerKind</c> and
/// current-model filters) and the real keyless <c>UnavailableReranker</c> stay production code under test.
/// Near/far vectors are seeded directly as <see cref="EmbeddingRecord"/> rows, blended away from the actual query
/// vector the double reports for the exact query text — never a pre-computed neighbour set handed to the tool
/// (QA independence, per the issue body).
/// </para>
/// </remarks>
public sealed class SearchNewsToolIntegrationTests : IClassFixture<SearchNewsToolTestPostgresFactory>
{
    private const string Query = "gh988 Fed rate decision";
    private static readonly DateTimeOffset _publishedAt = new(2026, 8, 28, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid _foreignOperatorId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SearchNewsToolTestPostgresFactory _factory;

    private sealed record ToolResultItem(string Headline, string Source, DateTimeOffset PublishedAt, string Snippet);

    private sealed record ToolResults(List<ToolResultItem> Results);

    private sealed record ToolError(string Error);

    public SearchNewsToolIntegrationTests(SearchNewsToolTestPostgresFactory factory)
    {
        _factory = factory;
        _factory.EmbeddingProvider.Reset();
        ResetAsync().GetAwaiter().GetResult();
    }

    // =================================================================================================================
    // AC1a — nearest first, honoring the requested limit.
    // =================================================================================================================

    [Fact]
    public async Task SearchNews_ShouldReturnOnlyTheRequestedLimit_NearestFirst()
    {
        float[] queryVector = await QueryVectorAsync();
        Random random = new(988);

        // Four SoftSignal items at strictly increasing cosine distance from the query (weightOther ascending), seeded
        // out of distance order so the assertion cannot pass by seed-order coincidence.
        await SeedSoftSignalAsync(random, queryVector, weightOther: 0.7, "https://news.test/gh988-farthest", "Farthest Fed headline");
        await SeedSoftSignalAsync(random, queryVector, weightOther: 0.1, "https://news.test/gh988-closest", "Closest Fed headline");
        await SeedSoftSignalAsync(random, queryVector, weightOther: 0.5, "https://news.test/gh988-third", "Third Fed headline");
        await SeedSoftSignalAsync(random, queryVector, weightOther: 0.3, "https://news.test/gh988-second", "Second Fed headline");

        ToolResults results = await SearchAsync(Query, limit: 2);

        results.Results.Select(r => r.Headline).Should().Equal(
            ["Closest Fed headline", "Second Fed headline"],
            "the two nearest by cosine distance, ascending — Take(limit) drops the farther two entirely");
    }

    // =================================================================================================================
    // AC1b — the SoftSignal filter (and, by construction, the partial IX_Embeddings_Vector_Cosine_SoftSignal index it
    // is built to keep matchable, gh#864): an item of a DIFFERENT owner kind must never surface, however close.
    // =================================================================================================================

    [Fact]
    public async Task SearchNews_ShouldExcludeOtherOwnerKinds_EvenWhenTheirVectorIsClosestPossible()
    {
        float[] queryVector = await QueryVectorAsync();
        Random random = new(989);

        // A Suggestion-owned row IDENTICAL to the query vector -- the closest possible distance (zero). If the
        // OwnerKind filter (or the partial index's predicate) were ever dropped or widened, this would rank #1.
        // Deliberately given a matching NewsRecord under its own DedupKey too (same shape a real hydrate would find)
        // -- WITHOUT that row, a broken filter would still be invisible to the tool's output: the recalled owner id
        // would have nothing to hydrate and would silently vanish at that later step regardless of the filter, and
        // this whole case would pass whether or not the OwnerKind filter actually ran (the guard-discipline trap:
        // "a test that passes both with and against the defect is documentation, not verification").
        await SeedEmbeddingAsync(
            EmbeddingOwnerKind.Suggestion, "https://news.test/gh988-wrong-kind", queryVector,
            url: "https://news.test/gh988-wrong-kind", title: "Wrong-kind headline that must never surface",
            summary: "This embedding is Suggestion-owned; search_news must never return it.");

        await SeedSoftSignalAsync(random, queryVector, weightOther: 0.2, "https://news.test/gh988-only-near", "Only near SoftSignal headline");
        await SeedSoftSignalAsync(random, queryVector, weightOther: 0.4, "https://news.test/gh988-only-far", "Only far SoftSignal headline");

        ToolResults results = await SearchAsync(Query, limit: 10);

        results.Results.Select(r => r.Headline).Should().Equal(
            ["Only near SoftSignal headline", "Only far SoftSignal headline"],
            "a Suggestion-owned row must never surface from search_news, however close its vector sits to the query");
    }

    // =================================================================================================================
    // AC2 — Global (R-20) read: an operator with no feedback of their own still gets the full global set, including
    // an item another operator has already rated. No accidental join/filter through SoftSignalFeedback.
    // =================================================================================================================

    [Fact]
    public async Task SearchNews_ShouldReturnTheSameGlobalNewsSet_EvenWhenAnotherOperatorHasAlreadyRatedAnItem()
    {
        float[] queryVector = await QueryVectorAsync();
        Random random = new(990);

        string ratedDedupKey = "https://news.test/gh988-rated-by-someone-else";
        await SeedSoftSignalAsync(random, queryVector, weightOther: 0.15, ratedDedupKey, "Rated-by-someone-else headline");
        await SeedSoftSignalAsync(random, queryVector, weightOther: 0.35, "https://news.test/gh988-unrated", "Unrated headline");

        // A FOREIGN operator's feedback -- never this test's own current-user context (resolved with no HTTP
        // identity at all, i.e. Guid.Empty -- an operator with genuinely NO feedback of their own). If retrieval
        // ever joined or filtered through SoftSignalFeedback, this row belonging to someone ELSE would either hide
        // the item or leak a cross-operator dependency.
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
            database.SoftSignalFeedbacks.Add(new SoftSignalFeedback
            {
                Id = Guid.NewGuid(),
                UserId = _foreignOperatorId,
                NewsDedupKey = ratedDedupKey,
                Kind = SoftSignalKind.ThumbsDown,
                CreatedAt = _publishedAt,
            });
            await database.SaveChangesAsync();
        }

        ToolResults results = await SearchAsync(Query, limit: 10);

        results.Results.Select(r => r.Headline).Should().Equal(
            ["Rated-by-someone-else headline", "Unrated headline"],
            "news is the R-20 global exception -- another operator's feedback must neither hide nor reorder it for an operator with none of their own");
    }

    // =================================================================================================================
    // AC3a — degrade to empty, never throw, when no embedding provider is available.
    // =================================================================================================================

    [Fact]
    public async Task SearchNews_ShouldReturnAnEmptyResultSet_WhenNoEmbeddingProviderIsAvailable()
    {
        await SeedSoftSignalAsync(new Random(991), await QueryVectorAsync(), 0.2, "https://news.test/gh988-unreachable", "Unreachable headline");
        _factory.EmbeddingProvider.IsAvailable = false;

        string raw = await ExecuteToolAsync(Query, limit: 5);

        raw.Should().Be("{\"results\":[]}", "an unavailable provider degrades to an honest empty result, never a throw");

        // The distinguishing side effect of the EARLY short-circuit (NewsRetrievalService.RetrieveAsync's own doc:
        // "an unavailable provider is short-circuited before the embed call, so a degraded deployment never pays
        // for -- or ledgers -- a query it would only discard"): if that guard were ever removed, the pipeline would
        // still return an empty result via the downstream null-vector check (this double returns a null vector when
        // unavailable), so the empty-JSON assertion above alone could not catch the regression -- this can.
        IReadOnlyList<AiUsageRecord> ledgerRows = await WithDatabaseReadAsync(
            database => database.AiUsage.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        ledgerRows.Should().NotContain(row => row.Feature == AiUsageFeature.Embed, "an unreachable provider must never be called, let alone billed");
    }

    // =================================================================================================================
    // AC3b — a genuine pgvector read fault fails CLOSED through the tool's own catch (distinct from the chat
    // endpoint's fail-OPEN catch that gh#996's suite already proves) -- never an unhandled throw.
    // =================================================================================================================

    [Fact]
    public async Task SearchNews_ShouldFailClosedWithAnErrorString_WhenThePgvectorReadFaults()
    {
        await SeedSoftSignalAsync(new Random(992), await QueryVectorAsync(), 0.2, "https://news.test/gh988-faulted", "Faulted headline");

        await FaultVectorColumnAsync();
        try
        {
            string raw = await ExecuteToolAsync(Query, limit: 5);

            ToolError error = Deserialize<ToolError>(raw);
            error.Error.Should().Be(
                "News could not be searched right now.",
                "an unexpected pgvector fault must fail CLOSED through the tool's own catch, never propagate as an unhandled exception");
        }
        finally
        {
            await RestoreVectorColumnAsync();
        }
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    private async Task<float[]> QueryVectorAsync()
    {
        EmbeddingResult embedded = await _factory.EmbeddingProvider.EmbedQueryAsync(Query, CancellationToken.None);
        return [.. embedded.Vector!];
    }

    private async Task SeedSoftSignalAsync(
        Random random, float[] queryVector, double weightOther, string dedupKey, string title)
    {
        float[] vector = Blend(queryVector, RandomUnit(random), weightOther);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.SoftSignal, dedupKey, vector, dedupKey, title, $"{title} summary text.");
    }

    private async Task SeedEmbeddingAsync(
        EmbeddingOwnerKind ownerKind, string ownerId, float[] vector, string? url, string? title, string? summary)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

        // A NewsRecord is written whenever the caller supplies one -- for ANY owner kind, not only SoftSignal. A
        // wrong-owner-kind guard needs its embedding to have a matching hydratable row too, or a broken OwnerKind
        // filter would recall it but hydration would still find nothing to show, hiding the very regression the
        // case exists to catch (see the wrong-kind case's own remarks).
        if (title is not null)
        {
            database.News.Add(new NewsRecord
            {
                DedupKey = ownerId,
                Type = "news",
                Url = url!,
                Title = title,
                Summary = summary!,
                PublishedAt = _publishedAt,
                Tickers = [],
                SourceFeeds = ["finnhub"],
                RecordedAt = _publishedAt,
            });
        }

        database.Embeddings.Add(new EmbeddingRecord
        {
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            Model = _factory.EmbeddingProvider.Model,
            Dimensions = TradingCopilotDbContext.EmbeddingDimensions,
            Embedding = new Vector(vector),
            ContentHash = $"gh988-{ownerId}",
            RecordedAt = _publishedAt,
        });

        await database.SaveChangesAsync();
    }

    private async Task<ToolResults> SearchAsync(string query, int limit) => Deserialize<ToolResults>(await ExecuteToolAsync(query, limit));

    private async Task<string> ExecuteToolAsync(string query, int limit)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IChatTool tool = scope.ServiceProvider.GetServices<IChatTool>().Single(t => t.Name == "search_news");
        string inputJson = JsonSerializer.Serialize(new { query, limit });
        return await tool.ExecuteAsync(inputJson, CancellationToken.None);
    }

    private static T Deserialize<T>(string json)
    {
        T? value = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }

    // Injects a REAL pgvector read fault at the database, mirroring NewsGroundingIntegrationTests' column rename --
    // never by doubling INewsEmbeddingSimilarity. PgVectorNewsSimilarity.NearestNewsAsync's CosineDistance query
    // resolves the "Embedding" column by name; renamed out from under it, the query throws a genuine
    // PostgresException the moment it runs, proving the TOOL's own catch (not the chat endpoint's) actually fires.
    private Task FaultVectorColumnAsync() => WithDatabaseAsync(database =>
        database.Database.ExecuteSqlRawAsync("""ALTER TABLE "Embeddings" RENAME COLUMN "Embedding" TO "EmbeddingGh988Faulted";"""));

    private Task RestoreVectorColumnAsync() => WithDatabaseAsync(database =>
        database.Database.ExecuteSqlRawAsync("""ALTER TABLE "Embeddings" RENAME COLUMN "EmbeddingGh988Faulted" TO "Embedding";"""));

    private async Task WithDatabaseAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }

    private async Task<T> WithDatabaseReadAsync<T>(Func<TradingCopilotDbContext, Task<T>> read)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await read(database);
    }

    private async Task ResetAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.SoftSignalFeedbacks.IgnoreQueryFilters().ExecuteDeleteAsync();
        await database.Embeddings.ExecuteDeleteAsync();
        await database.News.ExecuteDeleteAsync();
        // Every case in this class shares one container (IClassFixture, gh#121); a prior case's Embed ledger row
        // would otherwise still be there when the degrade case's own AiUsage assertion runs, failing it for a
        // reason that has nothing to do with THAT case's own production behaviour.
        await database.AiUsage.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    /// <summary>A uniformly-random unit vector -- Box-Muller normal samples, then normalized.</summary>
    private static float[] RandomUnit(Random random)
    {
        float[] v = new float[TradingCopilotDbContext.EmbeddingDimensions];
        for (int i = 0; i < v.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            v[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return Normalize(v);
    }

    /// <summary>Blends <paramref name="anchor"/> with <paramref name="other"/> and re-normalizes to a unit vector.</summary>
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
}
