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

    private static NewsRecord News(string dedupKey, DateTimeOffset? resolvedAt, params string[] tickers) => new()
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
        RelevanceResolvedAt = resolvedAt,
    };

    [Fact]
    public async Task ResolveAsync_ShouldMaterializeMatchesOntoUnresolvedNews()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            seed.News.Add(News("a", resolvedAt: null, "SPY"));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now, CancellationToken.None);

        resolved.Should().Be(1);
        NewsRecord stored = await context.News.SingleAsync();
        stored.MatchedInstruments.Should().Contain("ES");
        stored.RelevanceResolvedAt.Should().Be(Now);
    }

    [Fact]
    public async Task ResolveAsync_ShouldSkipAlreadyResolvedNews_WhenConfigUnchanged()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.News.Add(News("a", resolvedAt: Now, "SPY"));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now.AddMinutes(1), CancellationToken.None);

        resolved.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReResolve_WhenTheConfigChangedAfterTheLastResolution()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            seed.News.Add(News("a", resolvedAt: Now, "SPY")); // resolved earlier, but with empty matches
            seed.RelevanceConfigStates.Add(new RelevanceConfigState { Id = Guid.NewGuid(), UpdatedAt = Now.AddMinutes(5) });
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        int resolved = await Service(context).ResolveAsync(Now.AddMinutes(10), CancellationToken.None);

        resolved.Should().Be(1); // stale: resolved before the config changed
        (await context.News.SingleAsync()).MatchedInstruments.Should().Contain("ES");
    }

    [Fact]
    public async Task ResolveAsync_ShouldBeIdempotent()
    {
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = "SPY", Instrument = "ES" });
            seed.News.Add(News("a", resolvedAt: null, "SPY"));
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext context = Context();
        NewsRelevanceService service = Service(context);
        await service.ResolveAsync(Now, CancellationToken.None);
        int second = await service.ResolveAsync(Now.AddMinutes(1), CancellationToken.None);

        second.Should().Be(0); // nothing left to resolve
    }
}
