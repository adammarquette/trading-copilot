using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Flatten;

/// <summary>
/// Deciding, per instrument, whether this process has earned the right to tell an external monitor it is flat
/// (gh#244, R-13, ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// The monitor pages on <b>absence</b>, so the two failure directions are asymmetric and both are covered here.
/// Reporting flat while exposed is a <i>lie on a safety path</i> — it withdraws the page at exactly the moment it
/// was earned. Failing to report on a quiet day is a false page, which trains the operator to mute the pager, and
/// a muted pager is worse than none because it manufactures confidence.
/// </para>
/// <para>
/// Exposure is aggregated <b>across every account this process serves</b>: one flat account does not make the
/// instrument flat.
/// </para>
/// </remarks>
public class FlattenCheckInServiceTests
{
    private static VenueId Projectx => VenueId.Parse("projectx");

    private static VenueAccountId AccountA => VenueAccountId.Create(Projectx, "9001");

    private static VenueAccountId AccountB => VenueAccountId.Create(Projectx, "9002");

    // Summer date -> CDT (UTC-5), so 14:30 CT == 19:30 UTC. DST arithmetic itself is FlattenSchedule's concern.
    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 7, 15, hour, minute, 0, TimeSpan.Zero);

    private static DateOnly MarketDay => new(2026, 7, 15);

    private readonly IDeadMansSwitch _switch = A.Fake<IDeadMansSwitch>();

    public FlattenCheckInServiceTests()
    {
        A.CallTo(() => _switch.ReportFlatAsync(A<InstrumentId>._, A<CancellationToken>._)).Returns(true);
    }

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private FlattenCheckInService Service() =>
        new(
            new TradingCopilotDbContext(
                new DbContextOptionsBuilder<TradingCopilotDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
                new FixedUser(Guid.Empty)),
            A.Fake<IProjectXVenueFactory>(),
            _switch,
            Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
            Options.Create(new FlattenOptions()),
            NullLogger<FlattenCheckInService>.Instance);

    private static FlattenSchedule Schedule(string symbol, TimeOnly close, bool enabled = true) =>
        FlattenSchedule.Create(InstrumentId.Parse(symbol), enabled, deadlineOverride: null, sessionClose: close);

    private static string FrontMonth(string symbol) => symbol switch
    {
        "ES" => "CON.F.US.EP.U26",
        "GC" => "CON.F.US.GC.Q26",
        _ => $"CON.F.US.{symbol}.U26",
    };

    private static PositionSnapshot Pos(VenueAccountId account, string contractKey, int net) =>
        new(account, VenueContractId.Create(Projectx, contractKey), net, new Price(5_000m));

    private static ITradingVenue Venue(params (VenueAccountId Account, PositionSnapshot[] Positions)[] byAccount)
    {
        ITradingVenue venue = A.Fake<ITradingVenue>();
        A.CallTo(() => venue.Id).Returns(Projectx);
        A.CallTo(() => venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .ReturnsLazily((VenueAccountId a, CancellationToken _) => Task.FromResult<IReadOnlyList<PositionSnapshot>>(
                byAccount.FirstOrDefault(entry => entry.Account == a).Positions ?? []));
        A.CallTo(() => venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId i, CancellationToken _) =>
                Task.FromResult(new ResolvedContract(VenueContractId.Create(Projectx, FrontMonth(i.Symbol)), i)));
        return venue;
    }

    private Task<IReadOnlyDictionary<InstrumentId, DateOnly>> Run(
        ITradingVenue venue,
        IReadOnlyList<FlattenSchedule> schedules,
        DateTimeOffset now,
        IReadOnlyDictionary<InstrumentId, DateOnly>? sent = null,
        params VenueAccountId[] accounts) =>
        Service().ReportForAccountsAsync(
            accounts.Length == 0 ? [AccountA] : accounts,
            venue,
            schedules,
            now,
            sent ?? new Dictionary<InstrumentId, DateOnly>(),
            CancellationToken.None);

    // --- It reports when it may ---

    [Fact]
    public async Task ReportForAccountsAsync_ShouldReportFlat_WhenNoExposureRemainsPastTheDeadline()
    {
        ITradingVenue venue = Venue((AccountA, []));

        IReadOnlyDictionary<InstrumentId, DateOnly> sent =
            await Run(venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45));

        sent.Should().ContainKey(InstrumentId.Parse("ES")).WhoseValue.Should().Be(MarketDay);
        A.CallTo(() => _switch.ReportFlatAsync(InstrumentId.Parse("ES"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ReportForAccountsAsync_ShouldReportFlat_WhenTheSessionWasEntirelyQuiet()
    {
        // No position was ever opened. Get this wrong and every flat day pages, the operator mutes the monitor,
        // and the safety net is off -- the most consequential way this feature can fail.
        ITradingVenue venue = Venue((AccountA, []));

        IReadOnlyDictionary<InstrumentId, DateOnly> sent =
            await Run(venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 31));

        sent.Should().ContainKey(InstrumentId.Parse("ES"));
    }

    // --- It stays silent when it must ---

    [Fact]
    public async Task ReportForAccountsAsync_ShouldWithhold_WhenExposureRemainsPastTheDeadline()
    {
        // The load-bearing guard: this is precisely when the monitor must page.
        ITradingVenue venue = Venue((AccountA, [Pos(AccountA, "CON.F.US.EP.M25", 2)]));

        IReadOnlyDictionary<InstrumentId, DateOnly> sent =
            await Run(venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45));

        sent.Should().BeEmpty();
        A.CallTo(() => _switch.ReportFlatAsync(A<InstrumentId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReportForAccountsAsync_ShouldWithhold_WhenAnotherAccountStillHoldsTheInstrument()
    {
        // Aggregation across accounts. One flat account does not make ES flat, and a per-account check-in would
        // have reported it flat here.
        ITradingVenue venue = Venue((AccountA, []), (AccountB, [Pos(AccountB, "CON.F.US.EP.M25", 1)]));

        IReadOnlyDictionary<InstrumentId, DateOnly> sent =
            await Run(venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45), sent: null, AccountA, AccountB);

        sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ReportForAccountsAsync_ShouldReportOnlyTheFlatInstrument_WhenAnotherIsStillExposed()
    {
        // GC deadline 12:15, ES 14:30. At 14:45 both are due; only GC is flat.
        ITradingVenue venue = Venue((AccountA, [Pos(AccountA, "CON.F.US.EP.M25", 2)]));

        IReadOnlyDictionary<InstrumentId, DateOnly> sent = await Run(
            venue,
            [Schedule("ES", new TimeOnly(14, 30)), Schedule("GC", new TimeOnly(12, 15))],
            Utc(19, 45));

        sent.Should().ContainKey(InstrumentId.Parse("GC"));
        sent.Should().NotContainKey(InstrumentId.Parse("ES"));
    }

    [Fact]
    public async Task ReportForAccountsAsync_ShouldNotReport_WhenTheMarketIsDisabled()
    {
        // The operator turned the safety net off here (R-13's warned override), so the switch has nothing to
        // vouch for and must not issue an all-clear.
        ITradingVenue venue = Venue((AccountA, []));

        IReadOnlyDictionary<InstrumentId, DateOnly> sent =
            await Run(venue, [Schedule("ES", new TimeOnly(14, 30), enabled: false)], Utc(19, 45));

        sent.Should().BeEmpty();
        A.CallTo(() => _switch.ReportFlatAsync(A<InstrumentId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- The gate keeps it cheap ---

    [Fact]
    public async Task ReportForAccountsAsync_ShouldNotReadPositions_WhenNothingIsDueYet()
    {
        // Before the deadline there is nothing to report, so the venue must not be polled once a minute all day.
        ITradingVenue venue = Venue((AccountA, []));

        await Run(venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(17, 0));

        A.CallTo(() => venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReportForAccountsAsync_ShouldNotReadPositions_WhenAlreadyReportedToday()
    {
        ITradingVenue venue = Venue((AccountA, []));
        Dictionary<InstrumentId, DateOnly> already = new() { [InstrumentId.Parse("ES")] = MarketDay };

        IReadOnlyDictionary<InstrumentId, DateOnly> sent =
            await Run(venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(20, 30), already);

        sent.Should().BeEmpty();
        A.CallTo(() => venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- A failed report is not a sent report ---

    [Fact]
    public async Task ReportForAccountsAsync_ShouldNotRecordTheDay_WhenTheMonitorRejectsTheReport()
    {
        // Recording it would mean never retrying, so a single transient failure would silently cost that day's
        // check-in -- and the monitor would page for a system that was actually fine.
        A.CallTo(() => _switch.ReportFlatAsync(A<InstrumentId>._, A<CancellationToken>._)).Returns(false);
        ITradingVenue venue = Venue((AccountA, []));

        IReadOnlyDictionary<InstrumentId, DateOnly> sent =
            await Run(venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45));

        sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ReportForAccountsAsync_ShouldReportTheOthers_WhenOneInstrumentsReportFails()
    {
        A.CallTo(() => _switch.ReportFlatAsync(InstrumentId.Parse("ES"), A<CancellationToken>._)).Returns(false);
        ITradingVenue venue = Venue((AccountA, []));

        IReadOnlyDictionary<InstrumentId, DateOnly> sent = await Run(
            venue,
            [Schedule("ES", new TimeOnly(14, 30)), Schedule("GC", new TimeOnly(12, 15))],
            Utc(19, 45));

        sent.Should().ContainKey(InstrumentId.Parse("GC"));
        sent.Should().NotContainKey(InstrumentId.Parse("ES"));
    }
}
