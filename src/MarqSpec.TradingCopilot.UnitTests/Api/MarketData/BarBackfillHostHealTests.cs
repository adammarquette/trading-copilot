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

    private static ITradingVenue Venue(Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<Bar>> respond)
    {
        ITradingVenue venue = A.Fake<ITradingVenue>();
        A.CallTo(() => venue.Id).Returns(Projectx);
        A.CallTo(() => venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId i, CancellationToken _) =>
                Task.FromResult(new ResolvedContract(VenueContractId.Create(Projectx, $"CON.F.US.{i.Symbol}.U26"), i)));
        A.CallTo(() => venue.GetBarsAsync(
                A<VenueContractId>._, A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
            .ReturnsLazily((VenueContractId _, DateTimeOffset from, DateTimeOffset to, TimeSpan _, CancellationToken _) =>
                Task.FromResult(respond(from, to)));
        return venue;
    }

    private static ServiceProvider Provider(string databaseName, ITradingVenue venue)
    {
        ServiceCollection services = new();
        services.AddSingleton<IProjectXVenueFactory>(new FixedVenueFactory(venue));
        services.AddScoped(_ => new TradingCopilotDbContext(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(databaseName).Options,
            new FixedUser(Guid.NewGuid())));
        services.AddScoped<BarBackfillService>();
        services.AddScoped<BarStoreHealService>();
        return services.BuildServiceProvider();
    }

    private static BarBackfillHost Host(IServiceProvider services, int maxHealWindowMinutes = 10080) =>
        new(services,
            Options.Create(new BarBackfillOptions
            {
                Instruments = ["ES"],
                ResolutionMinutes = [1],
                LookbackMinutes = 60,
                SessionClose = "16:00",
                MaxHealWindowMinutes = maxHealWindowMinutes,
            }),
            NullLogger<BarBackfillHost>.Instance,
            now: () => Now,
            delay: (_, token) => Task.FromCanceled(token)); // stop after the heal pass

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private sealed class FixedVenueFactory(ITradingVenue venue) : IProjectXVenueFactory
    {
        public ITradingVenue Create(FirmConventions conventions) => venue;
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHealBeforePolling_WhenTheStoreHasAHole()
    {
        // The store holds a series with a hole three hours back — far outside the 60-minute lookback. Only the
        // heal pass can see it: the periodic pass's window never reaches that far. This is the card's reason to
        // exist.
        string databaseName = Guid.NewGuid().ToString();
        await using (TradingCopilotDbContext seed = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(databaseName).Options,
            new FixedUser(Guid.NewGuid())))
        {
            for (int minute = -200; minute < -180; minute++)
            {
                if (minute != -190)
                {
                    seed.Bars.Add(new BarRecord
                    {
                        Venue = "projectx",
                        Instrument = "ES",
                        ResolutionMinutes = 1,
                        BucketStart = Now.AddMinutes(minute),
                        Open = 5000m,
                        High = 5010m,
                        Low = 4990m,
                        Close = 5005m,
                        Volume = 100,
                        RecordedAt = Now,
                    });
                }
            }

            await seed.SaveChangesAsync();
        }

        ITradingVenue venue = Venue((from, to) => [Bar(Now.AddMinutes(-190))]);
        await using ServiceProvider provider = Provider(databaseName, venue);
        BarBackfillHost host = Host(provider);

        await host.StartAsync(CancellationToken.None);
        await host.ExecuteTask!;

        await using (TradingCopilotDbContext check = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(databaseName).Options,
            new FixedUser(Guid.NewGuid())))
        {
            (await check.Bars.AnyAsync(bar => bar.BucketStart == Now.AddMinutes(-190)))
                .Should().BeTrue("the startup heal pass must fill holes the narrow poll lookback cannot reach");
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotHeal_WhenNothingIsConfigured()
    {
        // Opt-in idiom: unconfigured means idle, never "heal everything" — and the heal pass must not resolve a
        // venue when there is nothing to heal (venue construction needs credentials a bare host may lack).
        string databaseName = Guid.NewGuid().ToString();
        ServiceCollection services = new();
        services.AddSingleton<IProjectXVenueFactory>(new FixedVenueFactory(
            A.Fake<ITradingVenue>(options => options.ThrowsOnFakeCreation())));
        services.AddScoped(_ => new TradingCopilotDbContext(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(databaseName).Options,
            new FixedUser(Guid.NewGuid())));
        services.AddScoped<BarBackfillService>();
        services.AddScoped<BarStoreHealService>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        BarBackfillHost host = new(
            provider,
            Options.Create(new BarBackfillOptions()),
            NullLogger<BarBackfillHost>.Instance,
            now: () => Now,
            delay: (_, token) => Task.FromCanceled(token));

        await host.StartAsync(CancellationToken.None);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepPolling_WhenTheHealPassFails()
    {
        // A heal failure (venue down at startup) must degrade to the ordinary poll loop — history is catchable
        // up, and losing the poller because the one-time heal failed would be the worse failure direction.
        string databaseName = Guid.NewGuid().ToString();
        ITradingVenue venue = Venue((from, to) => throw new InvalidOperationException("venue down at startup"));
        await using ServiceProvider provider = Provider(databaseName, venue);

        BarBackfillHost host = new(
            provider,
            Options.Create(new BarBackfillOptions
            {
                Instruments = ["ES"],
                ResolutionMinutes = [1],
                LookbackMinutes = 60,
                SessionClose = "16:00",
            }),
            NullLogger<BarBackfillHost>.Instance,
            now: () => Now,
            delay: (_, token) => Task.FromCanceled(token));

        await host.StartAsync(CancellationToken.None);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync(
            "a failed heal pass must degrade to the poll loop, not stop the host or the application");
    }
}
