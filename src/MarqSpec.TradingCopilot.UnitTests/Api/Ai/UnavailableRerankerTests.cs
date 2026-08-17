using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The default reranker (gh#975): the one registered until the Cohere rerank provider is configured, so the substrate
/// is usable — and every downstream test runs — with no API key and no spend. It has no HTTP dependency at all, so it
/// can never reach the network; it passes the candidates through in their original order.
/// </summary>
/// <remarks>
/// Rerank is a reordering refinement, so with no provider the correct degrade is the first-stage order, not an empty
/// list and not a throw — the passthrough counterpart of <see cref="UnavailableEmbeddingProvider"/> returning a null
/// vector rather than a zero one.
/// </remarks>
public class UnavailableRerankerTests
{
    private static UnavailableReranker Reranker(ILogger<UnavailableReranker>? logger = null) =>
        new(logger ?? NullLogger<UnavailableReranker>.Instance);

    private static IReadOnlyList<string> Docs(int count) =>
        Enumerable.Range(0, count).Select(i => $"doc-{i}").ToList();

    [Fact]
    public void IsAvailable_ShouldBeFalse_SoCallersCanBranchRatherThanDiscoverThroughFailure()
    {
        Reranker().IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task RerankAsync_ShouldReturnTheIdentityOrder_RatherThanReorderingOrDropping()
    {
        RerankResult result = await Reranker().RerankAsync("anything", Docs(4), topN: 4, CancellationToken.None);

        result.Ranking.Select(r => r.Index).Should().Equal(new[] { 0, 1, 2, 3 }, "no provider means the first-stage order stands");
        result.Outcome.Should().Be(RerankOutcome.Failed);
        result.BilledSearches.Should().Be(0);
        result.EstimatedCostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task RerankAsync_ShouldHonorTopN_WhenPassingThrough()
    {
        RerankResult result = await Reranker().RerankAsync("anything", Docs(5), topN: 2, CancellationToken.None);

        result.Ranking.Select(r => r.Index).Should().Equal(new[] { 0, 1 }, "the identity fallback still keeps at most top-n");
    }

    [Fact]
    public async Task RerankAsync_ShouldReturnAnEmptyRanking_WhenThereAreNoCandidates()
    {
        RerankResult result = await Reranker().RerankAsync("anything", [], topN: 5, CancellationToken.None);

        result.Ranking.Should().BeEmpty();
    }

    [Fact]
    public async Task RerankAsync_ShouldAnnounceOnlyOnce_SoAConfigurationStateIsNotALogFlood()
    {
        // A retrieval loop calls this repeatedly. An error per call turns one configuration fact into noise, and a
        // flooding error is one that gets filtered out — taking the real signal with it.
        CountingLogger logger = new();
        UnavailableReranker reranker = Reranker(logger);

        for (int i = 0; i < 5; i++)
        {
            await reranker.RerankAsync("anything", Docs(2), topN: 2, CancellationToken.None);
        }

        logger.Errors.Should().Be(1);
    }

    [Fact]
    public void Model_ShouldBeNamed_SoAMeteredRowNeverClaimsAModelThatDidNotRank()
    {
        Reranker().Model.Should().Be("none");
    }

    [Fact]
    public void Reranker_ShouldSatisfyTheSeam()
    {
        Reranker().Should().BeAssignableTo<IReranker>();
    }

    private sealed class CountingLogger : ILogger<UnavailableReranker>
    {
        public int Errors { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Errors++;
            }
        }
    }
}
