using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The restart heal pass for the clean-historical bar store (gh#696, R-1): on ingestion start the durable
/// tables are the anchor — interior holes are backfilled and the tail resumes forward, bounded by the venue's
/// retention and idempotent by construction.
/// </summary>
/// <remarks>
/// This is NOT <see cref="GapBackfillService"/>: that one recovers <i>consumers</i> of the event log from a
/// retention gap. This one heals the bar store itself, whose missing history is state to rebuild rather than a
/// cursor to advance (gh#696's "distinct from the hub cursor" boundary).
/// </remarks>
public class BarStoreHealServiceTests
{
    private static VenueId Projectx => VenueId.Parse("projectx");

    /// <summary>Tuesday 2026-07-21 10:00 UTC — mid-session, no calendar edge cases in play.</summary>
    private static DateTimeOffset Now => new(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

    private static BarBackfillOptions Config(int maxHealWindowMinutes = 10080) => new()
    {
        Instruments = ["ES"],
        ResolutionMinutes = [1],
        LookbackMinutes = 120,
        SessionClose = "16:00",
        MaxHealWindowMinutes = maxHealWindowMinutes,
    };

    private static Bar Bar(DateTimeOffset open, decimal close = 5000m, long volume = 100) =>
        new(open, new Price(5000m), new Price(5010m), new Price(4990m), new Price(close), volume);

    private static BarRecord Record(DateTimeOffset bucket, int resolution = 1, string instrument = "ES") => new()
    {
        Venue = "projectx",
        Instrument = instrument,
        ResolutionMinutes = resolution,
        BucketStart = bucket,
        Open = 5000m,
        High = 5010m,
        Low = 4990m,
        Close = 5005m,
        Volume = 100,
        RecordedAt = Now,
    };

    /// <summary>A venue whose history answer depends on the requested window — heal windows are wide, polls narrow.</summary>
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

    private static BarStoreHealService Service(TradingCopilotDbContext context, BarBackfillOptions? options = null) =>
        new(context, Options.Create(options ?? Config()), NullLogger<BarStoreHealService>.Instance);

    // --- The core recovery: interior holes heal from the venue ---

    [Fact]
    public async Task HealAsync_ShouldFillAnInteriorHole_WhenTheVenueHasTheMissingBars()
    {
        // The store holds 09:00–09:59 except 09:30 — the classic restart/outage hole. The venue still has it.
        await using TradingCopilotDbContext context = Context();
        DateTimeOffset hole = Now.AddMinutes(-30);
        for (int minute = -60; minute < 0; minute++)
        {
            if (minute != -30)
            {
                context.Bars.Add(Record(Now.AddMinutes(minute)));
            }
        }

        await context.SaveChangesAsync();
        ITradingVenue venue = Venue((from, to) =>
            [Bar(hole)]); // the venue answers the hole's bucket whatever window is asked for

        BarStoreHealOutcome outcome = await Service(context).HealAsync(venue, Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(60);
        (await context.Bars.AnyAsync(bar => bar.BucketStart == hole)).Should().BeTrue();
        outcome.Series.Single().Written.Should().Be(1);
    }

    [Fact]
    public async Task HealAsync_ShouldResumeForward_WhenTheStoreIsBehind()
    {
        // The store stops at 09:30; three buckets (09:30–09:59) have since closed and are missing.
        await using TradingCopilotDbContext context = Context();
        for (int minute = -60; minute < -30; minute++)
        {
            context.Bars.Add(Record(Now.AddMinutes(minute)));
        }

        await context.SaveChangesAsync();
        ITradingVenue venue = Venue((from, to) =>
            [.. Enumerable.Range(-30, 30).Select(minute => Bar(Now.AddMinutes(minute)))]);

        await Service(context).HealAsync(venue, Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(60);
    }

    [Fact]
    public async Task HealAsync_ShouldNotDuplicate_WhenRunTwice()
    {
        // Idempotence is the acceptance criterion: re-running heals without duplicating rows.
        await using TradingCopilotDbContext context = Context();
        context.Bars.Add(Record(Now.AddMinutes(-2)));
        await context.SaveChangesAsync();
        ITradingVenue venue = Venue((from, to) => [Bar(Now.AddMinutes(-2)), Bar(Now.AddMinutes(-1))]);
        BarStoreHealService service = Service(context);

        await service.HealAsync(venue, Now, CancellationToken.None);
        await service.HealAsync(venue, Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task HealAsync_ShouldSkipTheStillFormingBucket_WhenTheVenueReturnsIt()
    {
        // Same guard as the periodic pass: a bucket that has not closed by `now` must not be stored as final.
        await using TradingCopilotDbContext context = Context();
        context.Bars.Add(Record(Now.AddMinutes(-2)));
        await context.SaveChangesAsync();
        ITradingVenue venue = Venue((from, to) => [Bar(Now.AddMinutes(-1)), Bar(Now)]); // Now's bucket is open

        await Service(context).HealAsync(venue, Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(2);
        (await context.Bars.AnyAsync(bar => bar.BucketStart == Now)).Should().BeFalse();
    }

    // --- The anchor: heal starts from what is already stored ---

    [Fact]
    public async Task HealAsync_ShouldDoNothing_WhenTheStoreIsEmptyForTheSeries()
    {
        // No stored rows means no anchor: the periodic lookback pass seeds a fresh series, heal never fetches
        // "everything" (the opt-in idiom the backfill itself was written around).
        await using TradingCopilotDbContext context = Context();
        ITradingVenue venue = Venue((from, to) => [Bar(Now.AddMinutes(-1))]);

        BarStoreHealOutcome outcome = await Service(context).HealAsync(venue, Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(0);
        A.CallTo(() => venue.GetBarsAsync(
                A<VenueContractId>._, A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        outcome.Series.Single().Written.Should().Be(0);
    }

    [Fact]
    public async Task HealAsync_ShouldBoundTheWindow_ToTheConfiguredMaximum()
    {
        // The store reaches back ten days; the venue's retention cannot, so the heal anchors on the configured
        // window rather than asking for history the venue will refuse or truncate.
        await using TradingCopilotDbContext context = Context();
        context.Bars.Add(Record(Now.AddDays(-10)));
        context.Bars.Add(Record(Now.AddMinutes(-2)));
        await context.SaveChangesAsync();
        DateTimeOffset? firstFrom = null;
        ITradingVenue venue = Venue((from, to) =>
        {
            firstFrom ??= from;
            return [];
        });

        await Service(context, Config(maxHealWindowMinutes: 2880)).HealAsync(venue, Now, CancellationToken.None);

        firstFrom.Should().NotBeNull();
        firstFrom!.Value.Should().Be(Now.AddMinutes(-2880));
    }

    [Fact]
    public async Task HealAsync_ShouldFetchInChunks_WhenTheWindowExceedsOneVenuePage()
    {
        // The ProjectX adapter caps a history fetch at 1000 bars; a 2500-minute heal window must therefore be
        // walked in pages rather than asked for in one call that silently truncates.
        await using TradingCopilotDbContext context = Context();
        context.Bars.Add(Record(Now.AddMinutes(-2500)));
        context.Bars.Add(Record(Now.AddMinutes(-2)));
        await context.SaveChangesAsync();
        int calls = 0;
        ITradingVenue venue = Venue((from, to) =>
        {
            calls++;
            return [];
        });

        await Service(context).HealAsync(venue, Now, CancellationToken.None);

        calls.Should().BeGreaterThanOrEqualTo(3, "2500 minutes of 1-minute bars cannot fit in one 1000-bar page");
    }

    // --- Residual honesty: what could not heal is reported, not silenced ---

    [Fact]
    public async Task HealAsync_ShouldReportResidualGaps_WhenTheVenueCannotCoverAHole()
    {
        // The venue no longer has the hole's bucket (retention). The series stays holed, and the outcome says
        // so — a heal that reported success over a hole it did not fill would be worse than no heal at all.
        await using TradingCopilotDbContext context = Context();
        DateTimeOffset hole = Now.AddMinutes(-30);
        for (int minute = -60; minute < 0; minute++)
        {
            if (minute != -30)
            {
                context.Bars.Add(Record(Now.AddMinutes(minute)));
            }
        }

        await context.SaveChangesAsync();
        ITradingVenue venue = Venue((from, to) => []); // the venue has nothing

        BarStoreHealOutcome outcome = await Service(context).HealAsync(venue, Now, CancellationToken.None);

        outcome.Series.Single().ResidualMissing.Should().Equal(hole);
        outcome.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task HealAsync_ShouldReportComplete_WhenTheSeriesHealedToContiguous()
    {
        await using TradingCopilotDbContext context = Context();
        for (int minute = -60; minute < 0; minute++)
        {
            context.Bars.Add(Record(Now.AddMinutes(minute)));
        }

        await context.SaveChangesAsync();
        ITradingVenue venue = Venue((from, to) => []);

        BarStoreHealOutcome outcome = await Service(context).HealAsync(venue, Now, CancellationToken.None);

        outcome.Series.Single().ResidualMissing.Should().BeEmpty();
        outcome.IsComplete.Should().BeTrue();
    }

    // --- Failure containment, same stance as the periodic pass ---

    [Fact]
    public async Task HealAsync_ShouldNotThrow_WhenTheVenueRefusesTheCapability()
    {
        await using TradingCopilotDbContext context = Context();
        context.Bars.Add(Record(Now.AddMinutes(-2)));
        await context.SaveChangesAsync();
        ITradingVenue venue = A.Fake<ITradingVenue>();
        A.CallTo(() => venue.Id).Returns(Projectx);
        A.CallTo(() => venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId i, CancellationToken _) =>
                Task.FromResult(new ResolvedContract(VenueContractId.Create(Projectx, $"CON.F.US.{i.Symbol}.U26"), i)));
        A.CallTo(() => venue.GetBarsAsync(
                A<VenueContractId>._, A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
            .Throws(new VenueCapabilityNotSupportedException(VenueCapability.HistoricalBars));

        Func<Task> act = () => Service(context).HealAsync(venue, Now, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HealAsync_ShouldContinueToTheNextSeries_WhenOneFails()
    {
        await using TradingCopilotDbContext context = Context();
        context.Bars.Add(Record(Now.AddMinutes(-2), instrument: "ES"));
        context.Bars.Add(Record(Now.AddMinutes(-2), instrument: "NQ"));
        await context.SaveChangesAsync();
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
            .ReturnsLazily((VenueContractId _, DateTimeOffset from, DateTimeOffset to, TimeSpan _, CancellationToken _) =>
                Task.FromResult<IReadOnlyList<Bar>>([Bar(Now.AddMinutes(-1))]));

        BarStoreHealOutcome outcome =
            await Service(context, new BarBackfillOptions
            {
                Instruments = ["ES", "NQ"],
                ResolutionMinutes = [1],
                SessionClose = "16:00",
            }).HealAsync(venue, Now, CancellationToken.None);

        outcome.Series.Single(series => series.Instrument == "NQ").Written.Should().Be(1);
    }

    [Fact]
    public async Task HealAsync_ShouldDoNothing_WhenNothingIsConfigured()
    {
        await using TradingCopilotDbContext context = Context();
        ITradingVenue venue = Venue((from, to) => [Bar(Now.AddMinutes(-1))]);

        BarStoreHealOutcome outcome = await Service(context, new BarBackfillOptions())
            .HealAsync(venue, Now, CancellationToken.None);

        outcome.Series.Should().BeEmpty();
        A.CallTo(() => venue.GetBarsAsync(
                A<VenueContractId>._, A<DateTimeOffset>._, A<DateTimeOffset>._, A<TimeSpan>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- Resolutions are independent series, same key discipline as the periodic pass ---

    [Fact]
    public async Task HealAsync_ShouldStampRecordedAtWithThePassInstant_NotTheBucketsOwnTime()
    {
        // RecordedAt answers "when WE recorded it", not "when it happened" (the bucket carries the latter). A
        // heal is ONE logical recording event: a recovered hole is stamped with the pass's instant, not the
        // hole's own age. Pinning this deliberately — the stamp is `now`, the same value the periodic pass uses
        // for its window end, so a heal and a poll write the same instant for the same bucket.
        await using TradingCopilotDbContext context = Context();
        DateTimeOffset hole = Now.AddMinutes(-30);
        for (int minute = -60; minute < 0; minute++)
        {
            if (minute != -30)
            {
                context.Bars.Add(Record(Now.AddMinutes(minute)));
            }
        }

        await context.SaveChangesAsync();
        ITradingVenue venue = Venue((from, to) => [Bar(hole)]);

        await Service(context).HealAsync(venue, Now, CancellationToken.None);

        BarRecord healed = await context.Bars.SingleAsync(bar => bar.BucketStart == hole);
        healed.RecordedAt.Should().Be(Now, "a heal is one recording event stamped with the pass instant");
    }

    [Fact]
    public async Task HealAsync_ShouldKeepResolutionsApart_WhenTwoAreConfigured()
    {
        await using TradingCopilotDbContext context = Context();
        context.Bars.Add(Record(Now.AddMinutes(-10), resolution: 1));
        context.Bars.Add(Record(Now.AddMinutes(-10), resolution: 5));
        await context.SaveChangesAsync();
        ITradingVenue venue = Venue((from, to) => [Bar(Now.AddMinutes(-5))]);

        await Service(context, new BarBackfillOptions
        {
            Instruments = ["ES"],
            ResolutionMinutes = [1, 5],
            SessionClose = "16:00",
        }).HealAsync(venue, Now, CancellationToken.None);

        (await context.Bars.CountAsync()).Should().Be(4);
        (await context.Bars.Where(bar => bar.ResolutionMinutes == 1).CountAsync()).Should().Be(2);
        (await context.Bars.Where(bar => bar.ResolutionMinutes == 5).CountAsync()).Should().Be(2);
    }
}
