using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Observability;

/// <summary>
/// The protection monitor (gh#370, ADR-0019): the periodic reconciliation that answers <b>"is any live position
/// standing with nothing at the exchange behind it?"</b> — ADR-0019's P1, and the condition its rules could not
/// alert on because nothing measured it.
/// </summary>
/// <remarks>
/// It reads <b>venue truth on both sides</b> — positions and working orders — and consults no local state, because
/// every local record of protection is a belief about the venue and this exists to catch a wrong belief.
/// </remarks>
public class ProtectionMonitorServiceTests
{
    private static VenueId Venue => VenueId.Parse("projectx");

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly IExecutionMetrics _metrics = A.Fake<IExecutionMetrics>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public ProtectionMonitorServiceTests() => A.CallTo(() => _venue.Id).Returns(Venue);

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.Empty));

    private ProtectionMonitorService Service(TradingCopilotDbContext context) =>
        new(context, _metrics, NullLogger<ProtectionMonitorService>.Instance);

    private async Task SeedAccountAsync(string venueAccountKey = "9001")
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();

        await using TradingCopilotDbContext seed = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(_operator));

        seed.Firms.Add(new Firm { Id = firmId, UserId = _operator, Name = "Topstep", Type = FirmType.PropFirm });
        seed.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = _operator,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = "topstep-main",
        });
        seed.Accounts.Add(new Account
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            ConnectionId = connectionId,
            VenueAccountKey = venueAccountKey,
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
        });
        await seed.SaveChangesAsync();
    }

    private void VenueReports(IReadOnlyList<PositionSnapshot> positions, IReadOnlyList<WorkingOrder> orders)
    {
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).Returns(positions);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._)).Returns(orders);
    }

    private static PositionSnapshot Position(string key, int net) =>
        new(VenueAccountId.Create(Venue, "9001"), VenueContractId.Create(Venue, key), net, new Price(5_000m));

    private static WorkingOrder Stop(string key) =>
        new("ORD-1", VenueContractId.Create(Venue, key), new Price(4_990m), LimitPrice: null);

    [Fact]
    public async Task ObserveAsync_ShouldReportAnUnprotectedPosition()
    {
        // The P1: a live position and nothing resting at the exchange to close it.
        await SeedAccountAsync();
        VenueReports([Position("MESU26", 2)], []);
        await using TradingCopilotDbContext context = Context();

        await Service(context).ObserveAsync(_venue, CancellationToken.None);

        A.CallTo(() => _metrics.SetPositionProtection(1, 1)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ObserveAsync_ShouldReportAProtectedPosition_AsOpenButNotUnprotected()
    {
        await SeedAccountAsync();
        VenueReports([Position("MESU26", 2)], [Stop("MESU26")]);
        await using TradingCopilotDbContext context = Context();

        await Service(context).ObserveAsync(_venue, CancellationToken.None);

        A.CallTo(() => _metrics.SetPositionProtection(1, 0)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ObserveAsync_ShouldPublishZero_WhenTheBookIsFlat()
    {
        // Publishing zero matters as much as publishing a count: a gauge that stops being written looks exactly
        // like one whose value never changed, so "flat" must be stated rather than inferred from silence.
        await SeedAccountAsync();
        VenueReports([], []);
        await using TradingCopilotDbContext context = Context();

        await Service(context).ObserveAsync(_venue, CancellationToken.None);

        A.CallTo(() => _metrics.SetPositionProtection(0, 0)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ObserveAsync_ShouldSumAcrossAccounts()
    {
        await SeedAccountAsync("9001");
        await SeedAccountAsync("9002");
        VenueReports([Position("MESU26", 2)], []);
        await using TradingCopilotDbContext context = Context();

        await Service(context).ObserveAsync(_venue, CancellationToken.None);

        // Both accounts report one unprotected position, so the census is the total exposure, not one account's.
        A.CallTo(() => _metrics.SetPositionProtection(2, 2)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ObserveAsync_ShouldPublishNothing_WhenTheVenueCannotBeRead()
    {
        // Fail CLOSED on the measurement: an unreachable venue means the census is UNKNOWN, and publishing a
        // stale or invented zero would read as "nothing unprotected" — turning an outage into a false all-clear
        // on the one metric ADR-0019 pages on. Leaving the gauge unwritten lets the staleness itself be the
        // signal (the P1 on a silent telemetry pipeline covers it).
        await SeedAccountAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable"));
        await using TradingCopilotDbContext context = Context();

        await Service(context).ObserveAsync(_venue, CancellationToken.None);

        A.CallTo(() => _metrics.SetPositionProtection(A<int>._, A<int>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ObserveAsync_ShouldTreatAnUnsupportedWorkingOrderRead_AsUnknown_NotAsUnprotected()
    {
        // GetWorkingOrdersAsync is a default-throwing capability (R-17). A venue that cannot list working orders
        // tells us nothing about protection — reporting every position unprotected would page continuously on a
        // venue limitation, which is how a P1 gets muted.
        await SeedAccountAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([Position("MESU26", 2)]);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new NotSupportedException("This venue does not support listing working orders."));
        await using TradingCopilotDbContext context = Context();

        await Service(context).ObserveAsync(_venue, CancellationToken.None);

        A.CallTo(() => _metrics.SetPositionProtection(A<int>._, A<int>._)).MustNotHaveHappened();
    }
}
