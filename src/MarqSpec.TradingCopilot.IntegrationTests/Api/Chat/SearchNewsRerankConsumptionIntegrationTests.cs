using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Chat.Tools;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api.Chat;

/// <summary>
/// Independent QA for gh#988 (of gh#987) — the <c>search_news</c> pipeline's <b>rerank consumption</b>: proves the
/// list <see cref="MarqSpec.TradingCopilot.Api.Ai.NewsRetrievalService"/> returns is genuinely the reranker's own
/// order, not a silently-substituted recall order, and that no candidate is dropped along the way. Kept in its own
/// class (and its own container) because it needs a DIFFERENT <see cref="IReranker"/> composition —
/// <see cref="AdversarialReranker"/> in place of the keyless default —
/// set on <see cref="SearchNewsToolTestPostgresFactory.Reranker"/> before the host is first touched.
/// </summary>
/// <remarks>
/// The sibling <c>SearchNewsToolIntegrationTests</c> already proves the keyless-passthrough case (no reranker
/// configured, so recall order stands unchanged) as the natural consequence of production's own default
/// composition — repeating that assertion here would be redundant. This class instead proves the OTHER half: when
/// a reranker genuinely reorders, the tool's output reflects it. <see cref="AdversarialReranker"/> reverses its
/// input deterministically, so a bug that ignored <c>RerankResult.Ranking</c> (returning the hydrated recall list
/// unreordered) would leave the ascending-distance recall order in place instead of the expected reversed one, and
/// this case would go red.
/// </remarks>
public sealed class SearchNewsRerankConsumptionIntegrationTests : IClassFixture<SearchNewsToolTestPostgresFactory>
{
    private const string Query = "gh988 rerank consumption query";
    private static readonly DateTimeOffset _publishedAt = new(2026, 8, 28, 15, 0, 0, TimeSpan.Zero);

    private readonly SearchNewsToolTestPostgresFactory _factory;

    private sealed record ToolResultItem(string Headline);

    private sealed record ToolResults(List<ToolResultItem> Results);

    public SearchNewsRerankConsumptionIntegrationTests(SearchNewsToolTestPostgresFactory factory)
    {
        _factory = factory;
        _factory.Reranker = new AdversarialReranker(); // must be set before the host is first touched below
        _factory.EmbeddingProvider.Reset();
        ResetAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task SearchNews_ShouldReturnTheRerankersOrder_NotTheRecallOrder_AndDropNoCandidate()
    {
        EmbeddingResult embedded = await _factory.EmbeddingProvider.EmbedQueryAsync(Query, CancellationToken.None);
        float[] queryVector = [.. embedded.Vector!];
        Random random = new(993);

        // Recall order (ascending cosine distance from the query) is 1st, 2nd, 3rd, 4th. AdversarialReranker
        // reverses whatever it is handed, so the tool's output must come back 4th, 3rd, 2nd, 1st -- the OPPOSITE of
        // recall order -- proving the returned list is the reranker's, not the untouched recall order.
        await SeedAsync(random, queryVector, 0.10, "https://news.test/gh988-rerank-1st", "1st nearest headline");
        await SeedAsync(random, queryVector, 0.25, "https://news.test/gh988-rerank-2nd", "2nd nearest headline");
        await SeedAsync(random, queryVector, 0.40, "https://news.test/gh988-rerank-3rd", "3rd nearest headline");
        await SeedAsync(random, queryVector, 0.55, "https://news.test/gh988-rerank-4th", "4th nearest headline");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IChatTool tool = scope.ServiceProvider.GetServices<IChatTool>().Single(t => t.Name == "search_news");
        string raw = await tool.ExecuteAsync(JsonSerializer.Serialize(new { query = Query, limit = 4 }), CancellationToken.None);
        ToolResults results = JsonSerializer.Deserialize<ToolResults>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        results.Results.Select(r => r.Headline).Should().Equal(
            ["4th nearest headline", "3rd nearest headline", "2nd nearest headline", "1st nearest headline"],
            "the reranker reverses recall order deterministically -- the returned list must reflect ITS order, "
            + "and all four candidates must survive the round trip (none dropped)");
    }

    private async Task SeedAsync(Random random, float[] queryVector, double weightOther, string dedupKey, string title)
    {
        float[] vector = Blend(queryVector, RandomUnit(random), weightOther);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.News.Add(new NewsRecord
        {
            DedupKey = dedupKey,
            Type = "news",
            Url = dedupKey,
            Title = title,
            Summary = $"{title} summary text.",
            PublishedAt = _publishedAt,
            Tickers = [],
            SourceFeeds = ["finnhub"],
            RecordedAt = _publishedAt,
        });
        database.Embeddings.Add(new EmbeddingRecord
        {
            OwnerKind = EmbeddingOwnerKind.SoftSignal,
            OwnerId = dedupKey,
            Model = _factory.EmbeddingProvider.Model,
            Dimensions = TradingCopilotDbContext.EmbeddingDimensions,
            Embedding = new Vector(vector),
            ContentHash = $"gh988-rerank-{dedupKey}",
            RecordedAt = _publishedAt,
        });
        await database.SaveChangesAsync();
    }

    private async Task ResetAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Embeddings.ExecuteDeleteAsync();
        await database.News.ExecuteDeleteAsync();
    }

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
