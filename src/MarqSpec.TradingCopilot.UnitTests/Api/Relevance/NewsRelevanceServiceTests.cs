using MarqSpec.TradingCopilot.Api.Relevance;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Relevance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Relevance;

/// <summary>
/// The news-relevance resolution pass (gh#359): it materializes matched instruments / topics onto news, resolving
/// what is unresolved or stale-since-a-config-change, and re-running is idempotent.
/// </summary>
public class NewsRelevanceServiceTests
{
    private static DateTimeOffset Now => new(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

    private static NewsRelevanceService Service(TradingCopilotDbContext context) =>
        new(context, NullLogger<NewsRelevanceService>.Instance);

    private static NewsRecord News(string dedupKey, long? resolvedVersion, params string[] tickers) => new()
    {
        DedupKey = dedupKey,
        Type = "news",
        Url = $"https://site.com/{dedupKey}",
        Title = "FOMC holds rates",
        Summary = "The committee left rates unchanged.",
        PublishedAt = Now,
        Tickers = [.. tickers],
        SourceFeeds = ["finnhub"],
        RecordedAt = Now,
        RelevanceVersion = resolvedVersion,
    };

    private static RelevanceConfigState ConfigAt(long version) =>
        new() { Id = RelevanceConfigState.SingletonId, UpdatedAt = Now, Version = version };

    [Fact]
    public async Task ResolveAsync_ShouldMaterializeMatchesOntoNeverResolvedNews()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            seed.News.Add(News("a", resolvedVersion: null, "SPY")); // null version == never resolved
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now, CancellationToken.None);

        resolved.Should().Be(1);
        NewsRecord stored = await context.News.SingleAsync();
        stored.MatchedInstruments.Should().Contain("ES");
        stored.RelevanceVersion.Should().Be(0, "resolved against config generation 0 (no edits yet)");
        stored.RelevanceResolvedAt.Should().Be(Now, "the observability timestamp is still stamped");
    }

    [Fact]
    public async Task ResolveAsync_ShouldSkipNews_WhenItsVersionMatchesTheConfigGeneration()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.News.Add(News("a", resolvedVersion: 3, "SPY"));
            seed.RelevanceConfigStates.Add(ConfigAt(3));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now, CancellationToken.None);

        resolved.Should().Be(0, "the row is already resolved against the current generation");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReResolve_WhenTheConfigGenerationAdvanced()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            seed.News.Add(News("a", resolvedVersion: 4, "SPY")); // resolved against an older generation
            seed.RelevanceConfigStates.Add(ConfigAt(5));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now, CancellationToken.None);

        resolved.Should().Be(1);
        NewsRecord stored = await context.News.SingleAsync();
        stored.MatchedInstruments.Should().Contain("ES");
        stored.RelevanceVersion.Should().Be(5, "stamped with the generation it was resolved against");
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotDependOnWallClock_WhenTimeMovesBackwardsBetweenPasses()
    {
        // THE gh#418 GUARD. The old design compared timestamps, so a pass running with an EARLIER clock than a
        // prior resolution (clock skew across instances) could misjudge staleness. Version comparison cannot:
        // a config generation ahead of the row re-resolves regardless of what any clock says.
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            seed.News.Add(News("a", resolvedVersion: 1, "SPY"));
            seed.RelevanceConfigStates.Add(ConfigAt(2));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        // A clock a full day BEFORE the row's stored resolution time — nonsensical under a wall-clock scheme.
        int resolved = await Service(context).ResolveAsync(Now.AddDays(-1), CancellationToken.None);

        resolved.Should().Be(1, "generation 1 < 2, so it re-resolves — the clock is irrelevant");
    }

    [Fact]
    public async Task ResolveAsync_ShouldBeIdempotent()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            seed.News.Add(News("a", resolvedVersion: null, "SPY"));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        NewsRelevanceService service = Service(context);
        await service.ResolveAsync(Now, CancellationToken.None);
        int second = await service.ResolveAsync(Now.AddMinutes(1), CancellationToken.None);

        second.Should().Be(0); // nothing left to resolve — the version now matches
    }
}
