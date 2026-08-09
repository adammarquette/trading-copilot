using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The heal-first startup wiring of the bar backfill host (gh#696, R-1): one heal pass over the durable tables
/// before the periodic poll loop, in its own scope, so a restart heals the accumulated gap instead of leaving it
/// for the narrow lookback window to slowly crawl back through.
/// </summary>
public class BarBackfillHostHealTests
{
    private static VenueId Projectx => VenueId.Parse("projectx");

    private static DateTimeOffset Now => new(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);

    private static Bar Bar(DateTimeOffset open) =>
        new(open, new Price(5000m), new Price(5010m), new Price(4990m), new Price(5005m), 100);

    /// <summary>
    /// A venue that only answers bars inside the requested window — exactly what a real history API does, and
    /// what keeps these tests honest: if a bar lands, the pass that asked for its window must have run.
    /// </summary>
    private static ITradingVenue Venue(IReadOnlyList<Bar> history, bool throwsOnFetch = false)
    {
        ITradingVenue venue = A.Fake<ITradingVenue>();
        A.CallTo(() => venue.Id).Returns(Projectx);
        A.CallTo(() => venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId i, CancellationToken _) =>
                Task.FromResult(new ResolvedContract(VenueContractId.Create(Projectx, $"CON.F.US.{i.Symbol}.U26"), i)));

        if (throwsOnFetch)
        {
            A.CallTo(() => venue.GetBarsAsync(
                    A<VenueContractId>._, A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
                .Throws(new InvalidOperationException("venue down at startup"));
        }
        else
        {
            A.CallTo(() => venue.GetBarsAsync(
                    A<VenueContractId>._, A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
                .ReturnsLazily((VenueContractId _, DateTimeOffset from, DateTimeOffset to, TimeSpan _, CancellationToken _) =>
                    Task.FromResult<IReadOnlyList<Bar>>([.. history.Where(bar => bar.OpenTime >= from && bar.OpenTime < to)]));
        }

        return venue;
    }

    private static ServiceProvider Provider(string databaseName, ITradingVenue venue, BarBackfillOptions? options = null)
    {
        ServiceCollection services = new();
        services.AddLogging(); // the services take ILogger<T>; without it DI resolution fails inside the scope
        services.AddSingleton(Options.Create(options ?? Configured()));
        services.AddSingleton<IProjectXVenueFactory>(new FixedVenueFactory(venue));
        services.AddScoped(_ => new TradingCopilotDbContext(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(databaseName).Options,
            new FixedUser(Guid.NewGuid())));
        services.AddScoped<BarBackfillService>();
        services.AddScoped<BarStoreHealService>();
        return services.BuildServiceProvider();
    }

    private static async Task SeedAsync(string databaseName, IEnumerable<BarRecord> records)
    {
        await using TradingCopilotDbContext seed = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(databaseName).Options,
            new FixedUser(Guid.NewGuid()));
        seed.Bars.AddRange(records);
        await seed.SaveChangesAsync();
    }

    private static BarRecord Record(DateTimeOffset bucket) => new()
    {
        Venue = "projectx",
        Instrument = "ES",
        ResolutionMinutes = 1,
        BucketStart = bucket,
        Open = 5000m,
        High = 5010m,
        Low = 4990m,
        Close = 5005m,
        Volume = 100,
        RecordedAt = Now,
    };

    private static BarBackfillOptions Configured() => new()
    {
        Instruments = ["ES"],
        ResolutionMinutes = [1],
        LookbackMinutes = 60,
        SessionClose = "16:00",
    };

    private static BarBackfillHost Host(IServiceProvider services, CancellationTokenSource stop, BarBackfillOptions? options = null) =>
        new(services,
            Options.Create(options ?? Configured()),
            NullLogger<BarBackfillHost>.Instance,
            now: () => Now,
            // First delay ends the run: the heal pass has already happened, so whatever follows it is the loop.
            delay: (_, _) =>
            {
                stop.Cancel();
                return Task.CompletedTask;
            });

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private sealed class FixedVenueFactory(ITradingVenue venue) : IProjectXVenueFactory
    {
        public ITradingVenue Create(FirmConventions conventions) => venue;
    }

    private sealed class ThrowingVenueFactory : IProjectXVenueFactory
    {
        public ITradingVenue Create(FirmConventions conventions) =>
            throw new InvalidOperationException("must not resolve a venue when nothing is configured");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHealBeforePolling_WhenTheStoreHasAHoleOutsideThePollLookback()
    {
        // The store holds a series with a hole three hours back — far outside the 60-minute poll lookback. The
        // venue only answers bars inside the window it is asked for, so only a pass that asks for the hole's
        // window can fill it. That pass is the heal, and it must run at startup.
        string databaseName = Guid.NewGuid().ToString();
        List<BarRecord> seeded =
            [.. Enumerable.Range(-200, 20).Where(minute => minute != -190).Select(minute => Record(Now.AddMinutes(minute)))];
        await SeedAsync(databaseName, seeded);

        ITradingVenue venue = Venue([Bar(Now.AddMinutes(-190))]);
        await using ServiceProvider provider = Provider(databaseName, venue);
        using CancellationTokenSource stop = new();
        BarBackfillHost host = Host(provider, stop);

        await host.StartAsync(stop.Token);
        await host.ExecuteTask!;

        await using TradingCopilotDbContext check = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(databaseName).Options,
            new FixedUser(Guid.NewGuid()));
        (await check.Bars.AnyAsync(bar => bar.BucketStart == Now.AddMinutes(-190)))
            .Should().BeTrue("the startup heal pass must fill holes the narrow poll lookback cannot reach");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotTouchTheVenue_WhenNothingIsConfigured()
    {
        // Opt-in idiom: unconfigured means idle, never "heal everything" — the host must return before resolving
        // a venue at all (venue construction needs credentials a bare host may lack).
        string databaseName = Guid.NewGuid().ToString();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(Options.Create(new BarBackfillOptions()));
        services.AddSingleton<IProjectXVenueFactory>(new ThrowingVenueFactory());
        services.AddScoped(_ => new TradingCopilotDbContext(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(databaseName).Options,
            new FixedUser(Guid.NewGuid())));
        services.AddScoped<BarBackfillService>();
        services.AddScoped<BarStoreHealService>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        using CancellationTokenSource stop = new();
        BarBackfillHost host = Host(provider, stop, new BarBackfillOptions());

        await host.StartAsync(stop.Token);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync(
            "with nothing configured the host returns before the heal pass or the poll loop touch the venue");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepPolling_WhenTheHealCannotFetch()
    {
        // A venue down at startup must degrade to the ordinary poll loop — history is catchable up, and losing
        // the poller because the one-time heal failed would be the worse failure direction. The assertion is
        // that the fetch seam was reached at least TWICE: once by the heal pass, once by the poll loop that
        // survived it.
        string databaseName = Guid.NewGuid().ToString();
        await SeedAsync(databaseName, [Record(Now.AddMinutes(-2))]);
        ITradingVenue venue = Venue([], throwsOnFetch: true);
        await using ServiceProvider provider = Provider(databaseName, venue);
        using CancellationTokenSource stop = new();
        BarBackfillHost host = Host(provider, stop);

        await host.StartAsync(stop.Token);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync("a failed heal must degrade to the poll loop, not stop the application");
        A.CallTo(() => venue.GetBarsAsync(
                A<VenueContractId>._, A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
            .MustHaveHappened(2, Times.OrMore);
    }
}
