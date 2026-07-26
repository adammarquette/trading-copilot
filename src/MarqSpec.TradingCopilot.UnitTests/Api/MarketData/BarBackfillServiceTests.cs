using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The clean-historical bar backfill (gh#302, R-1, ADR-0001) — the store ADR-0001 has always assumed and the
/// system never had.
/// </summary>
/// <remarks>
/// <para>
/// R-1 is explicit that the live stream and the historical series are stored separately and that the historical
/// one, refreshed from REST, is <b>the system of record</b> for bars used in journaling and replay. ADR-0001
/// leans on it too: a full indicator rebuild reprocesses this store, not the 24-hour event log.
/// </para>
/// <para>
/// Two failure modes carry the weight here, and both are quiet ones. A <b>duplicated</b> bar makes every
/// aggregate over it wrong while looking like more data; a <b>partial</b> bar stored as final looks like data
/// and is not, and everything derived from it inherits the error.
/// </para>
/// </remarks>
public class BarBackfillServiceTests
{
    private static VenueId Projectx => VenueId.Parse("projectx");

    private static DateTimeOffset Now => new(2026, 7, 20, 15, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

    private static BarBackfillOptions Config(params string[] instruments) => new()
    {
        Instruments = instruments.Length == 0 ? ["ES"] : instruments,
        ResolutionMinutes = [1],
        LookbackMinutes = 60,
    };

    private static Bar Bar(DateTimeOffset open, decimal close = 5000m, long volume = 100) =>
        new(open, new Price(5000m), new Price(5010m), new Price(4990m), new Price(close), volume);

    private static ITradingVenue Venue(IReadOnlyList<Bar> bars, Exception? throws = null)
    {
        ITradingVenue venue = A.Fake<ITradingVenue>();
        A.CallTo(() => venue.Id).Returns(Projectx);
        A.CallTo(() => venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId i, CancellationToken _) =>
                Task.FromResult(new ResolvedContract(VenueContractId.Create(Projectx, $"CON.F.US.{i.Symbol}.U26"), i)));

        if (throws is not null)
        {
            A.CallTo(() => venue.GetBarsAsync(
                    A<VenueContractId>._, A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
                .Throws(throws);
        }
        else
        {
            A.CallTo(() => venue.GetBarsAsync(
                    A<VenueContractId>._, A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
                .Returns(bars);
        }

        return venue;
    }

    private BarBackfillService Service(TradingCopilotDbContext context, BarBackfillOptions? options = null) =>
        new(context, Options.Create(options ?? Config()), NullLogger<BarBackfillService>.Instance);

    // --- It stores what it fetched ---

    [Fact]
    public async Task BackfillAsync_ShouldStoreTheFetchedBars()
    {
        await using TradingCopilotDbContext context = Context();
        ITradingVenue venue = Venue([Bar(Now.AddMinutes(-3)), Bar(Now.AddMinutes(-2))]);

        await Service(context).BackfillAsync(venue, Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task BackfillAsync_ShouldRecordTheBarsValues()
    {
        await using TradingCopilotDbContext context = Context();
        ITradingVenue venue = Venue([Bar(Now.AddMinutes(-3), close: 5123.25m, volume: 42)]);

        await Service(context).BackfillAsync(venue, Now, CancellationToken.None);

        BarRecord stored = await context.Bars.SingleAsync();
        stored.Close.Should().Be(5123.25m);
        stored.Volume.Should().Be(42);
        stored.Instrument.Should().Be("ES");
    }

    // --- Idempotence: the normal case, not an edge one ---

    [Fact]
    public async Task BackfillAsync_ShouldNotDuplicate_WhenTheSameWindowIsPolledTwice()
    {
        // Overlapping polls are how a periodic backfill WORKS -- each pass re-fetches a lookback window. If that
        // duplicated, every aggregate over the store would be wrong while looking like more data.
        await using TradingCopilotDbContext context = Context();
        ITradingVenue venue = Venue([Bar(Now.AddMinutes(-3)), Bar(Now.AddMinutes(-2))]);
        BarBackfillService service = Service(context);

        await service.BackfillAsync(venue, Now, CancellationToken.None);
        await service.BackfillAsync(venue, Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task BackfillAsync_ShouldUpdateInPlace_WhenTheVenueRestatesABar()
    {
        // Venues revise. The later value must win without leaving the earlier one beside it.
        await using TradingCopilotDbContext context = Context();
        DateTimeOffset bucket = Now.AddMinutes(-3);
        BarBackfillService service = Service(context);

        await service.BackfillAsync(Venue([Bar(bucket, close: 5000m, volume: 10)]), Now, CancellationToken.None);
        await service.BackfillAsync(Venue([Bar(bucket, close: 5077m, volume: 99)]), Now, CancellationToken.None);

        BarRecord stored = await context.Bars.SingleAsync();
        stored.Close.Should().Be(5077m);
        stored.Volume.Should().Be(99);
    }

    // --- Never store a bar that is still forming ---

    [Fact]
    public async Task BackfillAsync_ShouldSkipTheStillFormingBucket()
    {
        // THE quiet-failure guard. A half-formed bar written as final looks exactly like data, and every
        // indicator, journal entry and replay downstream inherits the error with nothing to signal it.
        // The adapter passes includePartialBar: false, but this must not depend on a venue behaving.
        await using TradingCopilotDbContext context = Context();
        ITradingVenue venue = Venue([Bar(Now.AddMinutes(-2)), Bar(Now)]); // the Now bucket has not closed

        await Service(context).BackfillAsync(venue, Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(1);
        (await context.Bars.SingleAsync()).BucketStart.Should().Be(Now.AddMinutes(-2));
    }

    [Fact]
    public async Task BackfillAsync_ShouldSkipABucketEndingExactlyAtNow()
    {
        // The boundary: a 1-minute bucket opening at 14:59 closes AT 15:00. At 15:00 it is complete.
        await using TradingCopilotDbContext context = Context();
        ITradingVenue venue = Venue([Bar(Now.AddMinutes(-1))]);

        await Service(context).BackfillAsync(venue, Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(1);
    }

    // --- Failure containment ---

    [Fact]
    public async Task BackfillAsync_ShouldNotThrow_WhenTheVenueRefusesTheCapability()
    {
        // R-17: a venue without HistoricalBars refuses at the seam. That is a configuration truth, not a crash --
        // the poller logs it and leaves the other instruments alone.
        await using TradingCopilotDbContext context = Context();
        ITradingVenue venue = Venue([], new VenueCapabilityNotSupportedException(VenueCapability.HistoricalBars));

        Func<Task> act = () => Service(context).BackfillAsync(venue, Now, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task BackfillAsync_ShouldContinueToTheNextInstrument_WhenOneFails()
    {
        // One bad instrument must not cost the rest of the watchlist its history.
        await using TradingCopilotDbContext context = Context();
        ITradingVenue venue = A.Fake<ITradingVenue>();
        A.CallTo(() => venue.Id).Returns(Projectx);
        A.CallTo(() => venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId i, CancellationToken _) =>
                Task.FromResult(new ResolvedContract(VenueContractId.Create(Projectx, $"CON.F.US.{i.Symbol}.U26"), i)));
        A.CallTo(() => venue.GetBarsAsync(
                A<VenueContractId>.That.Matches(c => c.Key.Contains("ES", StringComparison.Ordinal)),
                A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue hiccup"));
        A.CallTo(() => venue.GetBarsAsync(
                A<VenueContractId>.That.Matches(c => c.Key.Contains("NQ", StringComparison.Ordinal)),
                A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<Bar>>([Bar(Now.AddMinutes(-2))]);

        await Service(context, Config("ES", "NQ")).BackfillAsync(venue, Now, CancellationToken.None);

        (await context.Bars.SingleAsync()).Instrument.Should().Be("NQ");
    }

    // --- Opt-in ---

    [Fact]
    public async Task BackfillAsync_ShouldDoNothing_WhenNoInstrumentsAreConfigured()
    {
        // Independent of Ingestion:Symbols on purpose -- an operator may want history without a live stream, or
        // the reverse. Unconfigured means idle, never "fetch everything".
        await using TradingCopilotDbContext context = Context();
        ITradingVenue venue = Venue([Bar(Now.AddMinutes(-2))]);
        BarBackfillOptions none = new() { Instruments = [], ResolutionMinutes = [1] };

        await Service(context, none).BackfillAsync(venue, Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(0);
        A.CallTo(() => venue.GetBarsAsync(
                A<VenueContractId>._, A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- Resolutions are independent series ---

    [Fact]
    public async Task BackfillAsync_ShouldKeepResolutionsApart_WhenTwoAreConfigured()
    {
        // A 1-minute and a 5-minute bar can open at the same instant. Keyed only on time they would collide and
        // silently overwrite each other.
        await using TradingCopilotDbContext context = Context();
        BarBackfillOptions twoResolutions = new()
        {
            Instruments = ["ES"],
            ResolutionMinutes = [1, 5],
            LookbackMinutes = 60,
        };

        await Service(context, twoResolutions).BackfillAsync(
            Venue([Bar(Now.AddMinutes(-10))]), Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(2);
        (await context.Bars.Select(bar => bar.ResolutionMinutes).ToListAsync()).Should().BeEquivalentTo([1, 5]);
    }
}
