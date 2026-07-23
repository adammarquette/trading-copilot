using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// The <c>POST /accounts/{id}/orders</c> handler (gh#11, S3 increment 1) — the #74 gated send path finally
/// composed at the API. The safety-relevant behaviours: no declared risk profile ⇒ no send (fail-closed input),
/// a non-flat account ⇒ no send (unrealized P&amp;L would be guessed), a gate block persists its
/// <c>GateDecisionRecord</c> but never an <c>Order</c> row, and a placed order journals BOTH under the DB mode
/// guard.
/// </summary>
public class OrderEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly VenueAccountId _venueAccount = VenueAccountId.Create(VenueId.Parse("projectx"), "9001");

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public OrderEndpointsTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);

        // The executor's own venue identity -- the service's mismatch guard compares every handle against it,
        // and an unconfigured fake (default VenueId) reads as a foreign venue and refuses everything.
        A.CallTo(() => _venue.Id).Returns(VenueId.Parse("projectx"));
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.BracketOrders)); // can hold the safety stop (gh#11 inc 3)

        // A healthy, FLAT practice account at the venue unless a test says otherwise.
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice) { Stage = AccountStage.Practice },
        ]);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([]);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId instrument, CancellationToken _) => Task.FromResult(
                new ResolvedContract(VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26"), instrument)));
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));
    }

    private TradingCopilotDbContext Context(Guid? asUser = null)
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(asUser ?? _operator));
    }

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static IOptions<ProjectXConnectionOptions> PxOptions(string credentialKey = "topstep-main") =>
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = credentialKey });

    private static IOptions<ExecutionOptions> ExecOptions() =>
        Microsoft.Extensions.Options.Options.Create(new ExecutionOptions());

    private static HostTradingEnvironment Development { get; } = new(DeploymentEnvironment.Development);

    /// <summary>An MES buy risking 5 points to make 10, well inside every default cap.</summary>
    private static SendOrderRequest SmallBuy(int quantity = 1) => new(
        Symbol: "MES",
        TickSize: 0.25m,
        PointValue: 5m,
        Side: OrderSide.Buy,
        Quantity: quantity,
        Entry: 5300m,
        Stop: 5295m,
        SafetyStop: 5290m,
        ReferencePrice: 5300m,
        Type: OrderType.Market);

    private async Task<Guid> SeedAsync(TradingMode mode = TradingMode.Practice, bool withRiskProfile = true, string credentialKey = "topstep-main")
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm
        {
            Id = firmId,
            UserId = _operator,
            Name = "Topstep",
            Type = FirmType.PropFirm,
            StageConventions =
            [
                new FirmStageConvention { Id = Guid.NewGuid(), UserId = _operator, FirmId = firmId, Stage = AccountStage.Practice, CapitalAtRisk = false },
            ],
        });
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
            Mode = mode,
            CanTrade = true,
            IsVisible = true,
            Balance = 50_000m,
        });
        if (withRiskProfile)
        {
            context.RiskProfiles.Add(new RiskProfileRecord
            {
                Id = Guid.NewGuid(),
                UserId = _operator,
                AccountId = accountId,
                StartingBalance = 50_000m,
                FloorSource = FloorSource.FirmImposed,
                TrailingMode = TrailingMode.EndOfDay,
                TrailingAmount = 2_000m,
                PerTradeRiskFraction = 0.15m,
                TargetRewardRatio = 1.5m,
                MaxDrawdownPerTrade = 300m,
                DailyDrawdownGovernor = 600m,
                SizingBasis = SizingBasis.SafetyStop,
                MaxContractsPerOrder = 3,
            });
        }

        await context.SaveChangesAsync();
        return accountId;
    }

    private async Task<IResult> SendAsync(Guid accountId, SendOrderRequest request, string configuredKey = "topstep-main")
    {
        await using TradingCopilotDbContext context = Context();
        return await OrderEndpoints.SendOrderAsync(
            accountId, request, new FixedUser(_operator), context, _factory, PxOptions(configuredKey), ExecOptions(), Development, CancellationToken.None);
    }

    [Fact]
    public async Task SendOrder_ShouldReturnNotFound_ForAnUnknownAccount()
    {
        await SeedAsync();

        StatusOf(await SendAsync(Guid.NewGuid(), SmallBuy())).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenNoRiskProfileIsDeclared()
    {
        Guid accountId = await SeedAsync(withRiskProfile: false);

        IResult result = await SendAsync(accountId, SmallBuy());

        // The fail-closed input working as designed (gh#10): no declared limits, no send -- the gate never
        // fabricates permissive defaults.
        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenThisProcessHoldsADifferentCredentialKey()
    {
        Guid accountId = await SeedAsync(credentialKey: "someone-elses-login");

        IResult result = await SendAsync(accountId, SmallBuy(), configuredKey: "topstep-main");

        // One credential set per process (ADR-0015) -- the same guard discovery enforces, on the send path.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendOrder_ShouldRefuse_WhenTheAccountIsNotFlat()
    {
        Guid accountId = await SeedAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(
            [
                new PositionSnapshot(_venueAccount, VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26"), 1, new Price(5290m)),
            ]);

        IResult result = await SendAsync(accountId, SmallBuy());

        // Increment-1 honesty (gh#11): the venue reports no unrealized P&L, and guessing zero flatters a red
        // day. Flat is the only state where UnrealizedPnL = 0 is a fact rather than a guess.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendOrder_ShouldPersistTheBlockingDecision_AndNeverAnOrderRow_WhenTheGateBlocks()
    {
        Guid accountId = await SeedAsync();

        // Ten contracts against a 3-contract manual cap and a 300-per-trade budget: the gate must block or
        // resize; either way this proposal's full size never survives. Risking 100 points on MES ($500/contract)
        // makes even one contract exceed MaxDrawdownPerTrade -- an outright block.
        SendOrderRequest oversized = SmallBuy(quantity: 10) with { Stop = 5200m, SafetyStop = 5200m };

        IResult result = await SendAsync(accountId, oversized);

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();

        await using TradingCopilotDbContext reload = Context();
        GateDecisionRecord decision = await reload.GateDecisions.SingleAsync();
        decision.Outcome.Should().Be(GateOutcome.Blocked);
        decision.AccountId.Should().Be(accountId);
        decision.OrderId.Should().BeNull();                       // blocked-by-gate: decision row only,
        (await reload.Orders.AnyAsync()).Should().BeFalse();      // never an order row
    }

    [Fact]
    public async Task SendOrder_ShouldPlaceAndJournalBoth_WhenTheGateAllows()
    {
        Guid accountId = await SeedAsync();

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        await using TradingCopilotDbContext reload = Context();
        Order order = await reload.Orders.SingleAsync();
        order.AccountId.Should().Be(accountId);
        order.Mode.Should().Be(TradingMode.Practice);  // the DB mode-guard trigger's first real producer
        order.Status.Should().Be(OrderStatus.Working);
        order.VenueOrderKey.Should().Be("889001");
        order.UserId.Should().Be(_operator);

        GateDecisionRecord decision = await reload.GateDecisions.SingleAsync();
        decision.Outcome.Should().Be(GateOutcome.Allowed);
        decision.OrderId.Should().Be(order.Id);        // the audit pair, linked
    }

    [Fact]
    public async Task SendOrder_ShouldAuthorizeAgainstTheDeclaredMode_WhenTheVenueClaimsADifferentOne()
    {
        // The R-14 mode-source guard (gh#148, found by the gh#135 suite). ComposeAsync must hand the execution
        // service the operator's DECLARED mode, not the one the venue reports: gh#60 removed the venue flag as
        // the stake signal after it misclassified all 293 accounts on a real login.
        //
        // Declared Practice + venue claims Live, in Development: Practice trades anywhere, Live nowhere outside
        // production -- so the send succeeds only if the DECLARED mode is what reaches TradingModePolicy.
        // Reverting the override makes this fail 409 RefusedByMode.
        Guid accountId = await SeedAsync(mode: TradingMode.Practice);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Live)
            {
                Stage = AccountStage.Practice,
            },
        ]);

        IResult result = await SendAsync(accountId, SmallBuy());

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        // The journaled row carries the declared mode too -- the DB mode guard compares against that (gh#7).
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).Mode.Should().Be(TradingMode.Practice);
    }

    [Fact]
    public async Task SendOrder_ShouldRefuseADeclaredLiveAccount_EvenWhenTheVenueClaimsItIsPractice()
    {
        // The DANGEROUS direction of the same seam (gh#148): the venue calls a live account "practice" -- the
        // gh#60 failure exactly, where the venue's flag classified real funded accounts as practice. If the
        // guard read the venue's claim it would authorize a real-money trade from a Development host.
        Guid accountId = await SeedAsync(mode: TradingMode.Live);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "EXPRESS-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice)
            {
                Stage = AccountStage.Funded,
            },
        ]);

        IResult result = await SendAsync(accountId, SmallBuy());

        // Refused before sizing: Live may not be traded outside production, whatever the venue calls it.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.AnyAsync()).Should().BeFalse();
        (await reload.GateDecisions.AnyAsync()).Should().BeFalse(); // never sized, so no decision exists
    }

    [Fact]
    public async Task SendOrder_ShouldRefusePreGate_WhenTheAccountModeIsUndeclared()
    {
        Guid accountId = await SeedAsync(mode: TradingMode.Undeclared);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "50KTC-V2", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Undeclared) { Stage = AccountStage.Unknown },
        ]);

        IResult result = await SendAsync(accountId, SmallBuy());

        // Undeclared trades nowhere (gh#60) -- refused before sizing; no decision exists, so none is persisted.
        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.GateDecisions.AnyAsync()).Should().BeFalse();
        (await reload.Orders.AnyAsync()).Should().BeFalse();
    }
}
