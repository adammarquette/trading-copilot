using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The <c>GET /accounts/{id}/orders</c> shape (gh#381): what the venue-truth resting-orders read actually hands
/// a caller.
/// </summary>
/// <remarks>
/// The response is the contract an operator UI and a staging gate both read, so the two things that must not
/// drift are the <b>size</b> (the field the read exists for) and the <b>404 on an unowned account</b> (R-20
/// default-deny — existence itself is not disclosed). The <c>?instrument=</c> scope (gh#772) adds two distinct
/// client-mistake <b>400</b>s the boundary must name apart from an unreachable venue: a syntactically malformed
/// symbol, and a well-formed one the venue has <i>no contract</i> for.
/// </remarks>
public class WorkingOrderReconciliationEndpointsTests
{
    private const string CredentialKey = "topstep-main";

    private static VenueId Venue => VenueId.Parse("projectx");

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly IProjectXVenueFactory _venueFactory = A.Fake<IProjectXVenueFactory>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public WorkingOrderReconciliationEndpointsTests()
    {
        A.CallTo(() => _venue.Id).Returns(Venue);
        A.CallTo(() => _venueFactory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._))
            .Returns<IReadOnlyList<VenueAccount>>(
                [new VenueAccount(VenueAccountId.Create(Venue, "9001"), "PRAC-50K", 50_000m, true, true, TradingMode.Practice)]);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>([]);
    }

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(_operator));

    private WorkingOrderReconciliationService Service() =>
        new(Context(),
            _venueFactory,
            Options.Create(new ProjectXConnectionOptions { CredentialKey = CredentialKey }),
            Options.Create(new FlattenOptions()),
            NullLogger<WorkingOrderReconciliationService>.Instance);

    private async Task<Guid> SeedAccountAsync()
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await using TradingCopilotDbContext seed = Context();
        seed.Firms.Add(new Firm { Id = firmId, UserId = _operator, Name = "Topstep", Type = FirmType.PropFirm });
        seed.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = _operator,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = CredentialKey,
        });
        seed.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = _operator,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
        });
        await seed.SaveChangesAsync();
        return accountId;
    }

    [Fact]
    public async Task ReadAsync_ShouldBe404_WhenTheAccountIsNotFoundOrNotOwned()
    {
        // R-20 default-deny at the boundary: another operator's account is indistinguishable from one that does
        // not exist. Anything other than 404 here would disclose existence.
        IResult result = await WorkingOrderReconciliationEndpoints.ReadAsync(
            Guid.NewGuid(), instrument: null, Service(), Context(), CancellationToken.None);

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task ReadAsync_ShouldBe400_WhenTheInstrumentIsBlank()
    {
        // `?instrument=` scopes the read to one contract (gh#772); a blank symbol is a malformed request worth naming,
        // rejected before the venue is asked.
        IResult result = await WorkingOrderReconciliationEndpoints.ReadAsync(
            Guid.NewGuid(), "   ", Service(), Context(), CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .MustNotHaveHappened(); // rejected at the boundary -- the venue is never asked to resolve a blank symbol
    }

    [Fact]
    public async Task ReadAsync_ShouldBe400_WhenTheInstrumentResolvesToNoContract()
    {
        // gh#772 acceptance: the OTHER client mistake. A well-formed symbol the venue has no contract for parses fine,
        // reaches the venue, and the venue answers "no such contract" -- the endpoint names it 400, never an empty book
        // and never an Unknown-masquerade (which is reserved for an unreachable venue).
        Guid accountId = await SeedAccountAsync();
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .Throws(new ProjectXVenueException("No ProjectX contract matches instrument 'ZZZZ'."));

        IResult result = await WorkingOrderReconciliationEndpoints.ReadAsync(
            accountId, "ZZZZ", Service(), Context(), CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void RestingOrder_ShouldMarkAStopLegProtective_AndALimitLegNot()
    {
        // Surfaced explicitly so a caller does not re-derive the rule. A take-profit rests on the WINNING side —
        // it caps the gain and does nothing about the loss, so it is not protection.
        WorkingOrder stop = new("ORD-1", VenueContractId.Create(Venue, "MESU26"), new Price(4_990m), null, 2);
        WorkingOrder takeProfit = new("ORD-2", VenueContractId.Create(Venue, "MESU26"), null, new Price(5_050m), 2);

        (stop.StopPrice is not null).Should().BeTrue();
        (takeProfit.StopPrice is not null).Should().BeFalse();
    }

    [Fact]
    public void RestingOrdersResponse_ShouldCarryTheBasisAndEachOrdersSize()
    {
        RestingOrdersResponse response = new(
            PositionMarkBasis.Live.ToString(),
            [new RestingOrder("ORD-1", Guid.NewGuid(), "MESU26", 4_990m, null, 2, true)]);

        response.MarkBasis.Should().Be("Live");
        response.Orders[0].Size.Should().Be(2, "the size is the field this whole read exists to surface");
        response.Orders[0].IsProtective.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_ShouldCarryTheJournaledOrderId_WhenAVenueOrderMatchesAJournaledRow()
    {
        // gh#656: the venue-truth read keys orders by VenueOrderKey, but DELETE /orders/{id} and PATCH
        // /orders/{id}/price route on the app Order.Id (a GUID). So the read must surface the journaled id, matched
        // by venue key, or a UI holding only the venue key cannot act on what it lists. A venue-spawned protective
        // leg has no Order row (ADR-0007), so its OrderId is null and it is not actionable through /orders/{id}.
        Guid accountId = await SeedAccountAsync();
        Guid journaledId = await SeedWorkingOrderAsync(accountId, "ORD-ENTRY");
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>(
            [
                new WorkingOrder("ORD-ENTRY", VenueContractId.Create(Venue, "CON.F.US.MES.U26"), null, new Price(5_050m), 1),
                new WorkingOrder("ORD-LEG", VenueContractId.Create(Venue, "CON.F.US.MES.U26"), new Price(4_990m), null, 1),
            ]);

        IResult result = await WorkingOrderReconciliationEndpoints.ReadAsync(
            accountId, instrument: null, Service(), Context(), CancellationToken.None);

        RestingOrdersResponse response = result.Should().BeOfType<Ok<RestingOrdersResponse>>().Which.Value!;
        response.Orders.Single(order => order.VenueOrderKey == "ORD-ENTRY").OrderId
            .Should().Be(journaledId, "the journaled entry is actionable through /orders/{id}");
        response.Orders.Single(order => order.VenueOrderKey == "ORD-LEG").OrderId
            .Should().BeNull("a venue-spawned protective leg has no Order row, so it is not actionable through /orders/{id}");
    }

    [Fact]
    public async Task ReadAsync_ShouldLeaveOrderIdNull_WhenTheMatchingJournaledOrderIsNotWorking()
    {
        // A terminal order that once held this venue key must never be matched — acting on it through /orders/{id}
        // would be refused anyway, and surfacing its id as actionable would mislead. Only a Working row matches.
        Guid accountId = await SeedAccountAsync();
        await SeedWorkingOrderAsync(accountId, "ORD-GONE", OrderStatus.Cancelled);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>(
            [new WorkingOrder("ORD-GONE", VenueContractId.Create(Venue, "CON.F.US.MES.U26"), new Price(4_990m), null, 1)]);

        IResult result = await WorkingOrderReconciliationEndpoints.ReadAsync(
            accountId, instrument: null, Service(), Context(), CancellationToken.None);

        RestingOrdersResponse response = result.Should().BeOfType<Ok<RestingOrdersResponse>>().Which.Value!;
        response.Orders.Single().OrderId.Should().BeNull();
    }

    [Fact]
    public void MapRestingOrder_ShouldSurfaceTheHiddenWorkingStopAndBand_WhenThePlanIsHidden()
    {
        // gh#865: the working stop is PLATFORM-held (ADR-0007) -- it does NOT rest at the venue, so it rides back
        // alongside the venue-truth order. A Hidden plan is the only locally-movable one, so the move-stop UI needs
        // its price AND the safety -> working -> entry band to pre-validate a move before it ever calls the write.
        WorkingOrder entry = new("ORD-ENTRY", VenueContractId.Create(Venue, "MESU26"), null, new Price(5_050m), 2);
        WorkingStopGeometry plan = new(WorkingStopPrice: 4_990m, StopStaging.Hidden, SafetyStopPrice: 4_980m, EntryPrice: 5_000m);

        RestingOrder mapped = WorkingOrderReconciliationEndpoints.MapRestingOrder(entry, Guid.NewGuid(), plan);

        mapped.WorkingStopPrice.Should().Be(4_990m, "the hidden working stop is what the operator will move");
        mapped.StopStaging.Should().Be("Hidden");
        mapped.SafetyStopPrice.Should().Be(4_980m);
        mapped.EntryPrice.Should().Be(5_000m);
    }

    [Theory]
    [InlineData(StopStaging.Native)]
    [InlineData(StopStaging.Orphaned)]
    public void MapRestingOrder_ShouldSurfaceTheStaging_WhenThePlanIsNotHidden(StopStaging staging)
    {
        // The staging is LOAD-BEARING: only Hidden is locally movable. A Native plan is a venue order (moved there,
        // not here); an Orphaned one re-arms on reconnect. The UI gates on this, so it MUST be exposed verbatim even
        // off the hidden path -- surfacing the working stop without its staging would invite a move that cannot route.
        WorkingOrder entry = new("ORD-ENTRY", VenueContractId.Create(Venue, "MESU26"), new Price(4_990m), null, 1);
        WorkingStopGeometry plan = new(4_990m, staging, 4_980m, 5_000m);

        RestingOrder mapped = WorkingOrderReconciliationEndpoints.MapRestingOrder(entry, Guid.NewGuid(), plan);

        mapped.StopStaging.Should().Be(staging.ToString());
        mapped.WorkingStopPrice.Should().Be(4_990m, "the working stop is surfaced regardless of staging; the UI gates on staging");
    }

    [Fact]
    public void MapRestingOrder_ShouldLeaveEveryPlatformFieldNull_WhenThereIsNoPlan()
    {
        // An order with no staged-stop plan -- a bare entry, or a venue-spawned leg -- surfaces as pure venue truth:
        // none of the platform-held fields exist to show. The venue-truth fields (including IsProtective) still stand.
        WorkingOrder leg = new("ORD-LEG", VenueContractId.Create(Venue, "MESU26"), new Price(4_990m), null, 1);

        RestingOrder mapped = WorkingOrderReconciliationEndpoints.MapRestingOrder(leg, orderId: null, plan: null);

        mapped.WorkingStopPrice.Should().BeNull();
        mapped.StopStaging.Should().BeNull();
        mapped.SafetyStopPrice.Should().BeNull();
        mapped.EntryPrice.Should().BeNull();
        mapped.IsProtective.Should().BeTrue("the venue-truth stop leg still carries a stop trigger");
    }

    [Fact]
    public async Task ReadAsync_ShouldSurfaceThePlatformHeldWorkingStop_WhenAWorkingOrderHasAStopPlan()
    {
        // The relational wall (gh#865): the read already joins the journaled Order.Id (gh#656); it now also joins the
        // order's staged-stop plan, keyed by Order.Id and Working-scoped, so the hidden working stop rides back on the
        // venue-truth order for a move-stop UI to display.
        Guid accountId = await SeedAccountAsync();
        Guid journaledId = await SeedWorkingOrderAsync(accountId, "ORD-ENTRY");
        await SeedStopPlanAsync(journaledId, StopStaging.Hidden);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>(
            [new WorkingOrder("ORD-ENTRY", VenueContractId.Create(Venue, "CON.F.US.MES.U26"), new Price(4_990m), null, 1)]);

        IResult result = await WorkingOrderReconciliationEndpoints.ReadAsync(
            accountId, instrument: null, Service(), Context(), CancellationToken.None);

        RestingOrder order = result.Should().BeOfType<Ok<RestingOrdersResponse>>().Which.Value!.Orders.Single();
        order.OrderId.Should().Be(journaledId);
        order.WorkingStopPrice.Should().Be(4_990m);
        order.StopStaging.Should().Be("Hidden");
        order.SafetyStopPrice.Should().Be(4_980m);
        order.EntryPrice.Should().Be(5_000m);
    }

    [Fact]
    public async Task ReadAsync_ShouldNotSurfaceThePlan_WhenTheMatchingOrderIsTerminal()
    {
        // Working-scoped, exactly like the Order.Id join: a plan for a terminal order must never surface. The operator
        // cannot move a stop on an order that is gone, and showing its band would invite a move that can never route.
        Guid accountId = await SeedAccountAsync();
        Guid terminalId = await SeedWorkingOrderAsync(accountId, "ORD-GONE", OrderStatus.Cancelled);
        await SeedStopPlanAsync(terminalId, StopStaging.Hidden);
        A.CallTo(() => _venue.GetWorkingOrdersAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<WorkingOrder>>(
            [new WorkingOrder("ORD-GONE", VenueContractId.Create(Venue, "CON.F.US.MES.U26"), new Price(4_990m), null, 1)]);

        IResult result = await WorkingOrderReconciliationEndpoints.ReadAsync(
            accountId, instrument: null, Service(), Context(), CancellationToken.None);

        RestingOrder order = result.Should().BeOfType<Ok<RestingOrdersResponse>>().Which.Value!.Orders.Single();
        order.OrderId.Should().BeNull("a terminal order is not matched");
        order.WorkingStopPrice.Should().BeNull("and so its plan is never surfaced");
        order.StopStaging.Should().BeNull();
    }

    private async Task<Guid> SeedWorkingOrderAsync(
        Guid accountId, string venueOrderKey, OrderStatus status = OrderStatus.Working)
    {
        Guid orderId = Guid.NewGuid();
        await using TradingCopilotDbContext seed = Context();
        seed.Orders.Add(new Order
        {
            Id = orderId,
            UserId = _operator,
            AccountId = accountId,
            Instrument = "CON.F.US.MES.U26",
            Side = OrderSide.Buy,
            Size = 1,
            Type = OrderType.Limit,
            Status = status,
            Mode = TradingMode.Practice,
            VenueOrderKey = venueOrderKey,
            PlacedAt = DateTimeOffset.UnixEpoch,
        });
        await seed.SaveChangesAsync();
        return orderId;
    }

    private async Task SeedStopPlanAsync(Guid orderId, StopStaging staging)
    {
        // A valid Buy-side geometry (safety < actual < entry), owner-stamped so the read's query filter admits it.
        await using TradingCopilotDbContext seed = Context();
        seed.StopPlans.Add(new StopPlanRecord
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            OrderId = orderId,
            Side = OrderSide.Buy,
            EntryPrice = 5_000m,
            ActualStopPrice = 4_990m,
            SafetyStopPrice = 4_980m,
            ProximityMetric = StopProximityMetric.Ticks,
            ProximityValue = 4m,
            Staging = staging,
        });
        await seed.SaveChangesAsync();
    }
}
