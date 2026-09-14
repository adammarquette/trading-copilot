using MarqSpec.TradingCopilot.Api.Flatten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Flatten;

/// <summary>
/// The read behind the always-visible auto-flatten countdown (gh#657, R-13).
/// </summary>
/// <remarks>
/// <para>
/// This route exists because the schedule was previously reachable only through the startup <b>log</b>. A
/// countdown is the one part of the safety strip that cannot be derived client-side: the deadline is a wall-clock
/// Central time, and a browser resolving it against its own zone lands an hour out for the weeks either side of a
/// daylight-saving change — precisely when the operator is least likely to notice, and in the direction that
/// shows a deadline the scheduler will not act on.
/// </para>
/// <para>
/// So the contract is deliberately two-part: the server hands over both the deadline <b>and the instant it
/// considers "now"</b>. The client counts down the difference between them rather than between the deadline and
/// its own clock, which makes the display immune to a skewed workstation clock as well as to the zone.
/// </para>
/// </remarks>
public class FlattenScheduleEndpointsTests
{
    private static readonly DateTimeOffset _beforeAnyDeadline = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static FlattenScheduleResponse Read(FlattenOptions options, DateTimeOffset now) =>
        ((Ok<FlattenScheduleResponse>)FlattenScheduleEndpoints.GetSchedule(
            Options.Create(options), new FixedClock(now))).Value!;

    [Fact]
    public void GetSchedule_ShouldReturnEveryGovernedMarket_WhenTheScheduleIsRead()
    {
        FlattenScheduleResponse response = Read(new FlattenOptions(), _beforeAnyDeadline);

        response.Markets.Select(market => market.Instrument)
            .Should().BeEquivalentTo(["ES", "NQ", "CL", "GC"]);
    }

    [Fact]
    public void GetSchedule_ShouldStampTheServersOwnInstant_WhenTheScheduleIsRead()
    {
        // The half of the contract that makes the countdown trustworthy. Without `asOf` the client must subtract
        // the deadline from its OWN clock, and a workstation minutes fast shows minutes of safety margin that do
        // not exist -- an error in the dangerous direction, since it reads as more time than there is.
        Read(new FlattenOptions(), _beforeAnyDeadline).AsOf.Should().Be(_beforeAnyDeadline);
    }

    [Fact]
    public void GetSchedule_ShouldReturnOnlyDeadlinesStillAhead_WhenSomeHaveAlreadyPassed()
    {
        // 19:00 UTC is 14:00 CT: GC (12:15) and CL (13:15) are behind us, ES and NQ (14:30) are not. Every row
        // must still be in the future, or the strip renders a negative countdown for the rest of the day.
        DateTimeOffset betweenDeadlines = new(2026, 7, 15, 19, 0, 0, TimeSpan.Zero);

        FlattenScheduleResponse response = Read(new FlattenOptions(), betweenDeadlines);

        response.Markets.Should().OnlyContain(market => market.DeadlineUtc >= betweenDeadlines);
    }

    [Fact]
    public void GetSchedule_ShouldReportTheDeadlineInMarketWallClock_WhenTheScheduleIsRead()
    {
        // Central wall-clock, not the viewer's zone: it is what the CME rulebook and the operator's own habits
        // are expressed in, and what the startup log already reports.
        Read(new FlattenOptions(), _beforeAnyDeadline).Markets
            .Single(market => market.Instrument == "ES").Deadline.Should().Be("14:30");
    }

    [Fact]
    public void GetSchedule_ShouldSurfaceAnUnarmedMarket_WhenAutoFlattenIsDisabledForIt()
    {
        // R-13's deliberate, warned override. It must reach the strip as an explicit "not armed" rather than be
        // filtered out -- an absent market is indistinguishable from one that has not loaded, and the operator
        // would read the most dangerous configuration in the system as a rendering delay.
        FlattenOptions options = new()
        {
            Instruments = [new FlattenScheduleOption { Symbol = "ES", Enabled = false }],
        };

        FlattenMarketDeadline es = Read(options, _beforeAnyDeadline).Markets
            .Single(market => market.Instrument == "ES");

        es.Enabled.Should().BeFalse();
        es.Source.Should().Be(nameof(FlattenScheduleSource.ConfiguredOverride));
    }

    [Fact]
    public void GetSchedule_ShouldNameWhereEachDeadlineCameFrom_WhenNothingIsConfigured()
    {
        // Same provenance the startup report carries (gh#255): a dropped override and a built-in default resolve
        // to identical times, so only the source tells the operator which one is running.
        Read(new FlattenOptions(), _beforeAnyDeadline).Markets
            .Should().OnlyContain(market => market.Source == nameof(FlattenScheduleSource.BuiltInDefault));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
