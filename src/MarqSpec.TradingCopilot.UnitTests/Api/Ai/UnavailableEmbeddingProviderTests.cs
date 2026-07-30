using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The default embedding provider (gh#109): the one registered until Cohere (gh#403) lands, so the substrate is
/// usable — and every downstream test runs — with no API key and no spend.
/// </summary>
/// <remarks>
/// The failure it exists to prevent is a provider that <b>pretends</b>. An empty or zero-filled vector is a valid
/// input to a similarity search: it returns results, all equally meaningless. Retrieval that silently degrades to
/// noise is worse than retrieval that refuses, because it looks like an answer.
/// </remarks>
public class UnavailableEmbeddingProviderTests
{
    private static UnavailableEmbeddingProvider Provider(ILogger<UnavailableEmbeddingProvider>? logger = null) =>
        new(logger ?? NullLogger<UnavailableEmbeddingProvider>.Instance);

    [Fact]
    public void IsAvailable_ShouldBeFalse_SoCallersBranchRatherThanDiscoverThroughFailure()
    {
        Provider().IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task EmbedAsync_ShouldReturnNull_RatherThanAnEmptyOrZeroVector()
    {
        // The whole point. A zero-filled vector would rank every candidate identically and look like a working
        // search returning poor results — indistinguishable from a corpus with nothing relevant in it.
        (await Provider().EmbedAsync("anything", CancellationToken.None)).Vector.Should().BeNull();
    }

    [Fact]
    public async Task EmbedAsync_ShouldAnnounceOnlyOnce_SoAConfigurationStateIsNotALogFlood()
    {
        // A retrieval loop calls this repeatedly. An error per call turns one configuration fact into noise, and
        // a flooding error is one that gets filtered out — taking the real signal with it.
        CountingLogger logger = new();
        UnavailableEmbeddingProvider provider = Provider(logger);

        for (int i = 0; i < 5; i++)
        {
            await provider.EmbedAsync("anything", CancellationToken.None);
        }

        logger.Errors.Should().Be(1);
    }

    [Fact]
    public void Dimensions_ShouldMatchTheStoredColumnWidth()
    {
        // A provider whose width disagrees with the column cannot round-trip at all: pgvector rejects the insert
        // outright (proven against real Postgres — "expected 1024 dimensions"). Pinning it here means a change to
        // one is caught without a database.
        Provider().Dimensions.Should().Be(TradingCopilotDbContext.EmbeddingDimensions);
    }

    [Fact]
    public void Model_ShouldBeNamed_SoAStoredRowNeverClaimsAModelThatDidNotProduceIt()
    {
        Provider().Model.Should().Be("none");
    }

    [Fact]
    public void Provider_ShouldSatisfyTheSeam()
    {
        Provider().Should().BeAssignableTo<IEmbeddingProvider>();
    }

    private sealed class CountingLogger : ILogger<UnavailableEmbeddingProvider>
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
