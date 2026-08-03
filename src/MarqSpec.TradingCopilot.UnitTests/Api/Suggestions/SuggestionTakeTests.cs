using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Suggestions;

/// <summary>
/// Take (part A) — arming an order ticket from a suggestion (gh#548, R-4 / R-11b / R-12, ADR-0007). SAFETY-CRITICAL:
/// this is the first path that turns a suggestion into an order, so the suite is where the refusals live.
/// </summary>
/// <remarks>
/// The behaviours that matter: a take is refused unless the row is <b>currently</b> takeable — <see
/// cref="SuggestionState.Active"/>, inside its validity window <b>re-checked against the clock now</b> (not the
/// eventually-consistent flag), un-dispositioned, and with the market still within the entry tolerance <b>re-measured
/// at take time</b>; the contract spec is resolved from the server-side source and a miss <b>fails closed</b>, never a
/// client-supplied number; the catastrophic safety stop is <b>derived</b> from entry and the spec distance and its
/// ordering pre-checked so <c>CK_StopPlans_SafetyBeyondActual</c> can never trip; the size is the suggestion's, never
/// re-derived; the risk gate is untouched and still resizes/blocks; and a successful arm stamps
/// <see cref="Order.SuggestionId"/> and transmits nothing.
/// </remarks>
public class SuggestionTakeTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly VenueAccountId _venueAccount = VenueAccountId.Create(VenueId.Parse("projectx"), "9001");
    private static readonly DateTimeOffset _now = new(2026, 7, 30, 14, 0, 0, TimeSpan.Zero);

    // The real configuration-backed spec source, so the derivation runs against the shipped built-in ES spec
    // (0.25 tick, $50, 40 safety ticks => a 10-point catastrophic distance) rather than a fake that could agree
    // with a wrong implementation. "MES" is deliberately NOT among the defaults, so it is the fail-closed miss.
    private static readonly IInstrumentSpecSource _instrumentSpecs =
        new InstrumentSpecSource(Options.Create(new InstrumentSpecOptions()));

    private static readonly IOptions<SuggestionOptions> _options = Options.Create(new SuggestionOptions());

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public SuggestionTakeTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(VenueId.Parse("projectx"));
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.BracketOrders));
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice) { Stage = AccountStage.Practice },
        ]);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([]);
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId instrument, CancellationToken _) => Task.FromResult(
                new ResolvedContract(VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.ES.U26"), instrument)));
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));
    }

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static StagedOrderResponse StagedOf(IResult result) =>
        (StagedOrderResponse)((IValueHttpResult)result).Value!;

    private static IOptions<ProjectXConnectionOptions> PxOptions() =>
        Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" });

    // A generous notional ceiling: a full ES contract is ~$265k, over the 250k default, which would otherwise block
    // every ES order at the sanity layer before any account limit is reached. The tests exercise the account limits.
    private static IOptions<ExecutionOptions> ExecOptions() => Options.Create(new ExecutionOptions { MaxNotional = 5_000_000m });

    private static HostTradingEnvironment Development { get; } = new(DeploymentEnvironment.Development);

    // A generous, well-formed risk profile: the ES safety distance is 10 points ($500/contract), so these limits
    // let a 2-lot pass; individual tests tighten one limit to force a resize or a block.
    private async Task<Guid> SeedAccountAsync(int maxContractsPerOrder = 3, decimal maxDrawdownPerTrade = 2_000m)
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
            CredentialKey = "topstep-main",
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
        context.RiskProfiles.Add(new RiskProfileRecord
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            AccountId = accountId,
            StartingBalance = 50_000m,
            FloorSource = FloorSource.FirmImposed,
            TrailingMode = TrailingMode.EndOfDay,
            // Headroom the per-trade layer divides: the ES safety risk is $500/contract, so $20k of headroom at a 20%
            // per-trade fraction (a $4k budget => 8 contracts) leaves the manual/MaxDD caps to decide the size.
            TrailingAmount = 20_000m,
            PerTradeRiskFraction = 0.20m,
            TargetRewardRatio = 1.0m,
            MaxDrawdownPerTrade = maxDrawdownPerTrade,
            DailyDrawdownGovernor = 5_000m,
            SizingBasis = SizingBasis.SafetyStop,
            MaxContractsPerOrder = maxContractsPerOrder,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    private async Task<Guid> SeedSuggestionAsync(
        Guid? accountId = null,
        SuggestionState state = SuggestionState.Active,
        OrderSide side = OrderSide.Buy,
        decimal entry = 5_300m,
        decimal stop = 5_295m,
        decimal target = 5_320m,
        int size = 2,
        string instrument = "ES",
        DateTimeOffset? expiresAt = null,
        Guid? owner = null)
    {
        Guid id = Guid.NewGuid();
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Suggestions.Add(new Suggestion
        {
            Id = id,
            UserId = ownerId,
            AccountId = accountId ?? Guid.NewGuid(),
            Instrument = instrument,
            Side = side,
            Size = size,
            EntryPrice = entry,
            StopPrice = stop,
            TargetPrice = target,
            Mode = TradingMode.Practice,
            State = state,
            CreatedAt = _now.AddMinutes(-5),
            Rationale = "oversold bounce",
            CitedIndicator = "rsi",
            CitedPeriod = 14,
            CitedResolutionMinutes = 1,
            Confidence = 72,
            ExpiresAt = expiresAt ?? _now.AddMinutes(60),
        });
        await context.SaveChangesAsync();
        return id;
    }

    private async Task DisposeSuggestionAsync(Guid suggestionId)
    {
        await using TradingCopilotDbContext context = Context();
        context.SuggestionDispositions.Add(new SuggestionDisposition
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            SuggestionId = suggestionId,
            Kind = SuggestionDispositionKind.Passed,
            Reasons = SuggestionPassReason.None,
            CreatedAt = _now.AddMinutes(-1),
        });
        await context.SaveChangesAsync();
    }

    private async Task<IResult> TakeAsync(Guid id, decimal referencePrice = 5_300m, Guid? asUser = null, DateTimeOffset? now = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await SuggestionEndpoints.TakeAsync(
            id, new SuggestionTakeRequest(referencePrice), now ?? _now, new FixedUser(asUser ?? _operator), context,
            _instrumentSpecs, _options, _factory, PxOptions(), ExecOptions(), Development,
            A.Fake<IKillSwitch>(), NullExecutionMetrics.Instance, CancellationToken.None);
    }

    private async Task<int> OrderCountAsync()
    {
        await using TradingCopilotDbContext context = Context();
        return await context.Orders.CountAsync();
    }

    // ---- the refusal ladder: nothing takeable, nothing armed ----

    [Fact]
    public async Task TakeAsync_ShouldReturnNotFound_ForAnotherOperatorsSuggestion()
    {
        Guid mine = await SeedSuggestionAsync(owner: _operator);

        // 404, never 403: a stranger is not told the row exists, and cannot arm it (R-20).
        StatusOf(await TakeAsync(mine, asUser: _other)).Should().Be(StatusCodes.Status404NotFound);
        (await OrderCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TakeAsync_ShouldReturnNotFound_ForAnUnknownId()
    {
        StatusOf(await TakeAsync(Guid.NewGuid())).Should().Be(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData(SuggestionState.Stale)]
    [InlineData(SuggestionState.ExpiredVoid)]
    [InlineData(SuggestionState.Unknown)]
    public async Task TakeAsync_ShouldRefuse_WhenNotActive(SuggestionState state)
    {
        Guid id = await SeedSuggestionAsync(state: state);

        // Only Active is takeable; a stale/void/unknown row is refused with a reason, never armed.
        StatusOf(await TakeAsync(id)).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TakeAsync_ShouldRefuse_WhenActiveButThePastExpiry_ReCheckedAgainstTheClockNow()
    {
        // THE synchronous R-12 time re-check: the expiry sweep (gh#545) is eventually consistent, so a suggestion
        // one poll late is still Active with a past ExpiresAt. The take must re-measure the clock itself and refuse.
        Guid id = await SeedSuggestionAsync(state: SuggestionState.Active, expiresAt: _now.AddMinutes(-1));

        StatusOf(await TakeAsync(id, now: _now)).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TakeAsync_ShouldConflict_WhenAlreadyDispositioned()
    {
        Guid id = await SeedSuggestionAsync();
        await DisposeSuggestionAsync(id);

        // The operator already acted on this suggestion (a pass); a take conflicts rather than arming a second path.
        StatusOf(await TakeAsync(id)).Should().Be(StatusCodes.Status409Conflict);
        (await OrderCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TakeAsync_ShouldRefuse_WhenTheInstrumentHasNoSpec()
    {
        // MES is not among the built-in specs. A server-originated proposal must fail closed on a miss -- never a
        // client-supplied or defaulted tick size, which would silently mis-size the sizing ladder.
        Guid id = await SeedSuggestionAsync(instrument: "MES", entry: 5_300m, stop: 5_295m, target: 5_320m);

        StatusOf(await TakeAsync(id)).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TakeAsync_ShouldRefuse_WhenThePriceHasDriftedBeyondTolerance_EvenWhileActive()
    {
        // THE synchronous drift re-check: State is Active and inside its window, but the market has moved past the
        // entry tolerance (default 8 ticks = 2 points on ES). A drifted-but-still-Active row must be refused, not
        // armed, or R-12's "never silently transmits a stale ticket" is violated inside the consumer's lag window.
        Guid id = await SeedSuggestionAsync(state: SuggestionState.Active, entry: 5_300m, stop: 5_295m, target: 5_320m);

        StatusOf(await TakeAsync(id, referencePrice: 5_305m)).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TakeAsync_ShouldArm_WhenThePriceIsWithinTolerance()
    {
        // The boundary the drift check must allow: a reference exactly on the entry is zero drift, well inside the band.
        Guid accountId = await SeedAccountAsync();
        Guid id = await SeedSuggestionAsync(accountId: accountId);

        StatusOf(await TakeAsync(id, referencePrice: 5_300m)).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task TakeAsync_ShouldRefuse_WhenTheWorkingStopIsWiderThanTheSafetyFloor()
    {
        // The catastrophic safety stop is derived at entry - 10 points (5290 for a 5300 entry). A working stop BELOW
        // that (5285) sits beyond the floor, so the native safety bracket would fire tighter than the operator's stop
        // -- an inversion. The arm writes no StopPlan row, so CK_StopPlans_SafetyBeyondActual never sees it; this
        // in-code guard is the only backstop and must refuse before staging.
        Guid id = await SeedSuggestionAsync(side: OrderSide.Buy, entry: 5_300m, stop: 5_285m, target: 5_320m);

        StatusOf(await TakeAsync(id)).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TakeAsync_ShouldRefuse_WhenTheReferencePriceIsNotPositive()
    {
        Guid id = await SeedSuggestionAsync();

        // A zero/negative reference is not a price. Fail closed with a clear 400 rather than letting it read as drift.
        StatusOf(await TakeAsync(id, referencePrice: 0m)).Should().Be(StatusCodes.Status400BadRequest);
        (await OrderCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TakeAsync_ShouldRefuse_WithoutADeclaredRiskProfile()
    {
        // Arm is send-minus-transmission: the shared fail-closed ladder still applies. No risk profile => 422 (gh#10).
        Guid accountId = await SeedAccountAsync();
        await using (TradingCopilotDbContext strip = Context())
        {
            strip.RiskProfiles.RemoveRange(await strip.RiskProfiles.ToListAsync());
            await strip.SaveChangesAsync();
        }
        Guid id = await SeedSuggestionAsync(accountId: accountId);

        StatusOf(await TakeAsync(id)).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderCountAsync()).Should().Be(0);
    }

    // ---- the arm: a staged, editable ticket that transmits nothing ----

    [Fact]
    public async Task TakeAsync_ShouldArmAStagedTicket_StampTheSuggestionId_AndNeverTouchTheVenue()
    {
        Guid accountId = await SeedAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId: accountId, side: OrderSide.Buy, entry: 5_300m, stop: 5_295m, target: 5_320m, size: 2);

        IResult result = await TakeAsync(suggestionId, referencePrice: 5_300m);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        StagedOf(result).ApprovedQuantity.Should().Be(2, "the gate passes the suggestion's size cleanly under these limits");
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();

        await using TradingCopilotDbContext reload = Context();
        Order staged = await reload.Orders.SingleAsync();
        staged.Status.Should().Be(OrderStatus.Staged);
        staged.VenueOrderKey.Should().BeNull();
        staged.EntryMethod.Should().Be(OrderEntryMethod.ArmedTake);
        staged.SuggestionId.Should().Be(suggestionId, "the arm closes the suggestion->order traceability gap (gh#548)");
        staged.Side.Should().Be(OrderSide.Buy);
        staged.EntryPrice.Should().Be(5_300m);
        staged.WorkingStopPrice.Should().Be(5_295m);
        staged.TakeProfitPrice.Should().Be(5_320m);
        staged.SafetyStopPrice.Should().Be(5_290m, "the safety stop is derived from entry and the ES spec distance, not the request");
    }

    [Fact]
    public async Task TakeAsync_ShouldTakeTheSizeFromTheSuggestion_NeverReDeriveIt()
    {
        // The size came from the operator's trigger, not the model; even when the gate would authorize far more, the
        // staged row carries the suggestion's size unchanged.
        Guid accountId = await SeedAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId: accountId, size: 2);

        await TakeAsync(suggestionId);

        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).Size.Should().Be(2);
    }

    [Fact]
    public async Task TakeAsync_ShouldDeriveTheSafetyStopAboveEntry_ForAShort()
    {
        // Symmetry: a short's catastrophic floor is entry + 10 points, and its working stop must sit between entry
        // and that floor.
        Guid accountId = await SeedAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(
            accountId: accountId, side: OrderSide.Sell, entry: 5_300m, stop: 5_305m, target: 5_280m);

        IResult result = await TakeAsync(suggestionId, referencePrice: 5_300m);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        await using TradingCopilotDbContext reload = Context();
        Order staged = await reload.Orders.SingleAsync();
        staged.Side.Should().Be(OrderSide.Sell);
        staged.SafetyStopPrice.Should().Be(5_310m);
        staged.SuggestionId.Should().Be(suggestionId);
    }

    [Fact]
    public async Task TakeAsync_ShouldLeaveTheGateToResize_AsForAManualTicket()
    {
        // The risk gate is untouched and authoritative. A 1-lot manual cap resizes a 2-lot suggestion down; the
        // decision surfaces the resize, and the staged row still carries the suggestion's requested size.
        Guid accountId = await SeedAccountAsync(maxContractsPerOrder: 1);
        Guid suggestionId = await SeedSuggestionAsync(accountId: accountId, size: 2);

        IResult result = await TakeAsync(suggestionId);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        StagedOf(result).ApprovedQuantity.Should().Be(1, "the gate binds the manual cap, exactly as for a manual arm");
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.SingleAsync()).Size.Should().Be(2, "the requested size rides the staged row; the resize binds at send");
    }

    [Fact]
    public async Task TakeAsync_ShouldLeaveTheGateToBlock_AsForAManualTicket()
    {
        // A per-trade budget below one contract's risk ($500 on ES) authorizes zero. The ticket still stages -- a
        // blocked proposal is exactly what the operator edits until it passes (ADR-0007) -- but with no size the gate
        // grants, never a warning clicked past.
        Guid accountId = await SeedAccountAsync(maxDrawdownPerTrade: 100m);
        Guid suggestionId = await SeedSuggestionAsync(accountId: accountId, size: 2);

        IResult result = await TakeAsync(suggestionId);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        StagedOf(result).ApprovedQuantity.Should().Be(0, "a risk-layer breach is blocked, never a warning the operator clicks past");
    }

    [Fact]
    public async Task TakeAsync_ShouldConflict_WhenTheSuggestionAlreadyHasALiveOrder()
    {
        // Defensive anti-stacking (gh#548 review): part A writes no disposition, so this guard is what stops a
        // double-click on Approve from minting two Staged tickets off one suggestion (which gh#589's send-vs-take gap
        // could then transmit as double risk). The first arm succeeds; a second, while that ticket is live, conflicts.
        Guid accountId = await SeedAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId: accountId);

        StatusOf(await TakeAsync(suggestionId)).Should().Be(StatusCodes.Status200OK);
        StatusOf(await TakeAsync(suggestionId)).Should().Be(StatusCodes.Status409Conflict);

        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.CountAsync(order => order.SuggestionId == suggestionId))
            .Should().Be(1, "the second arm mints no second ticket");
    }

    [Fact]
    public async Task TakeAsync_ShouldRefuse_WhenTheWorkingStopIsWiderThanTheSafetyFloor_ForAShort()
    {
        // Symmetric to the long guard: a short's catastrophic floor is entry + 10 points (5310); a working stop ABOVE
        // it (5315) sits beyond the floor, so the native bracket would fire tighter than the operator's stop -- an
        // inversion refused before staging.
        Guid id = await SeedSuggestionAsync(side: OrderSide.Sell, entry: 5_300m, stop: 5_315m, target: 5_280m);

        StatusOf(await TakeAsync(id, referencePrice: 5_300m)).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await OrderCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TakeAsync_ShouldArm_WhenDriftIsExactlyAtTheTolerance()
    {
        // The boundary pins the strict `>` (not `>=`): 8 ticks x 0.25 = 2 points, so a reference exactly 2 points off
        // the entry is AT the band, not past it, and still arms. A future `>`->`>=` slip would redden this.
        Guid accountId = await SeedAccountAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId: accountId, entry: 5_300m);

        StatusOf(await TakeAsync(suggestionId, referencePrice: 5_302m)).Should().Be(StatusCodes.Status200OK);
    }
}
