using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The settlement-boundary position reconcile (gh#193): positions come from <b>venue truth</b>, tagged with their
/// mark basis. The safety-relevant behaviours: a venue that cannot be reached yields a <b>declared-unknown</b>
/// result with no positions (never a stale live view), the settlement window is reflected, and the R-20 boundary
/// holds (an account not owned by the caller is simply not found).
/// </summary>
public class PositionReconciliationServiceTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly VenueAccountId _venueAccount = VenueAccountId.Create(VenueId.Parse("projectx"), "9001");

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public PositionReconciliationServiceTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(VenueId.Parse("projectx"));
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice) { Stage = AccountStage.Practice },
        ]);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).Returns<IReadOnlyList<PositionSnapshot>>(
        [
            new PositionSnapshot(_venueAccount, VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26"), 2, new Price(5_300m)),
        ]);
    }

    private static DateTimeOffset CentralTime(int hour, int minute)
    {
        DateTime local = new(2026, 1, 15, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, MarketClock.CentralTime.GetUtcOffset(local));
    }

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options, new FixedUser(_operator));

    private PositionReconciliationService Service(FlattenOptions? flatten = null) => new(
        Context(),
        _factory,
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
        Microsoft.Extensions.Options.Options.Create(flatten ?? new FlattenOptions()),
        NullLogger<PositionReconciliationService>.Instance);

    private async Task<Guid> SeedAccountAsync(string credentialKey = "topstep-main")
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm { Id = firmId, UserId = _operator, Name = "Topstep", Type = FirmType.PropFirm });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = _operator,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = credentialKey,
        });
        context.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = _operator,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
            CanTrade = true,
            IsVisible = true,
            Balance = 50_000m,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    private static FlattenOptions EsSettlingAt16() =>
        new() { Instruments = [new FlattenScheduleOption { Symbol = "ES", Enabled = true, SessionClose = "16:00" }] };

    [Fact]
    public async Task Reconcile_ShouldReturnVenuePositions_WithLiveBasis_DuringTradingHours()
    {
        Guid accountId = await SeedAccountAsync();

        PositionReconciliation? result = await Service().ReconcileAsync(accountId, CentralTime(9, 0), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Basis.Should().Be(PositionMarkBasis.Live);
        result.Positions.Should().ContainSingle().Which.NetQuantity.Should().Be(2); // venue truth, surfaced
    }

    [Fact]
    public async Task Reconcile_ShouldTagSettlement_InsideTheMaintenanceWindow()
    {
        Guid accountId = await SeedAccountAsync();

        PositionReconciliation? result = await Service(EsSettlingAt16())
            .ReconcileAsync(accountId, CentralTime(16, 30), CancellationToken.None);

        result!.Basis.Should().Be(PositionMarkBasis.Settlement); // a carried position is a settlement re-mark, not live
    }

    [Fact]
    public async Task Reconcile_ShouldDeclareUnknown_WhenTheVenueCannotBeReached()
    {
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue down"));

        PositionReconciliation? result = await Service().ReconcileAsync(accountId, CentralTime(9, 0), CancellationToken.None);

        result!.Basis.Should().Be(PositionMarkBasis.Unknown); // fail-safe: declared-unknown, never a stale live view
        result.Positions.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_ShouldReturnNull_WhenTheAccountIsNotFound()
    {
        await SeedAccountAsync();

        PositionReconciliation? result = await Service().ReconcileAsync(Guid.NewGuid(), CentralTime(9, 0), CancellationToken.None);

        result.Should().BeNull(); // not found / not owned (R-20) -> the endpoint maps this to 404
    }

    [Fact]
    public async Task Reconcile_ShouldDeclareUnknown_WhenThisProcessHoldsADifferentCredentialKey()
    {
        Guid accountId = await SeedAccountAsync(credentialKey: "other-firm");

        PositionReconciliation? result = await Service().ReconcileAsync(accountId, CentralTime(9, 0), CancellationToken.None);

        result!.Basis.Should().Be(PositionMarkBasis.Unknown); // cannot reach this account's venue -> unobtainable
        result.Positions.Should().BeEmpty();
    }

    private static InstrumentId Es => InstrumentId.Parse("ES");

    private static VenueContractId EsContract => VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26");

    private static VenueContractId NqContract => VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MNQ.U26");

    [Fact]
    public async Task Reconcile_ShouldReturnOnlyTheInstrumentsPosition_WhenScopedToAnInstrument()
    {
        // gh#772: the chart overlays ONE instrument, so the scoped read filters to that instrument's contract — a
        // position in a different contract must not render on its chart.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(
            [
                new PositionSnapshot(_venueAccount, EsContract, 2, new Price(5_300m)),
                new PositionSnapshot(_venueAccount, NqContract, -1, new Price(19_900m)),
            ]);
        A.CallTo(() => _venue.ResolveContractAsync(Es, A<CancellationToken>._))
            .Returns(new ResolvedContract(EsContract, Es));

        PositionReconciliation? result = await Service()
            .ReconcileAsync(accountId, Es, CentralTime(9, 0), CancellationToken.None);

        result!.Positions.Should().ContainSingle().Which.Contract.Should().Be(EsContract);
    }

    [Fact]
    public async Task Reconcile_ShouldReturnEveryContract_WhenNotScopedToAnInstrument()
    {
        // The unscoped overload — and the 3-arg one that delegates to it with null, which auto-flatten and the
        // reconcile paths use — is unchanged: no resolve, every position.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(
            [
                new PositionSnapshot(_venueAccount, EsContract, 2, new Price(5_300m)),
                new PositionSnapshot(_venueAccount, NqContract, -1, new Price(19_900m)),
            ]);

        PositionReconciliation? result = await Service()
            .ReconcileAsync(accountId, CentralTime(9, 0), CancellationToken.None);

        result!.Positions.Should().HaveCount(2);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Reconcile_ShouldRaiseInstrumentNotResolvable_WhenTheVenueHasNoContractForTheInstrument()
    {
        // gh#772 acceptance: a well-formed symbol the venue has NO contract for is a CLIENT mistake, not unobtainable
        // truth. The read reached the venue (roster + positions), so the resolve's not-found is the venue answering "no
        // such contract" -- it must surface as a 400 (this exception, mapped at the endpoint), never Unknown (reserved
        // for an unreachable venue) and never an empty "flat on this instrument".
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .Throws(new ProjectXVenueException("No ProjectX contract matches instrument 'ES'."));

        Func<Task> read = () => Service().ReconcileAsync(accountId, Es, CentralTime(9, 0), CancellationToken.None);

        (await read.Should().ThrowAsync<InstrumentNotResolvableException>())
            .Which.Instrument.Should().Be(Es);
    }

    [Fact]
    public async Task Reconcile_ShouldDeclareUnknown_WhenResolvingTheContractHitsATransportFault()
    {
        // A transport fault mid-resolve is NOT the venue's not-found type, so it is genuine unobtainable truth --
        // exactly like an unreachable read: declared-unknown, never a spurious 400.
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue down mid-resolve"));

        PositionReconciliation? result = await Service()
            .ReconcileAsync(accountId, Es, CentralTime(9, 0), CancellationToken.None);

        result!.Basis.Should().Be(PositionMarkBasis.Unknown);
        result.Positions.Should().BeEmpty();
    }
}
