using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The conditional-order firing watcher's core (gh#198, ADR-0007): on a quote, fire the pending conditionals the
/// trigger crossed — through the authoritative fire-time re-gate — and cancel/expire the stale ones. The
/// safety-relevant behaviours: a fired order goes through the gate and journals its order + stop plan; a
/// gate-refused fire stays pending (never lost); a resolved order never re-fires; and nothing fires without its
/// trigger.
/// </summary>
public class ConditionalFiringServiceTests
{
    private const string Contract = "CON.F.US.MES.U26";
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly VenueAccountId _venueAccount = VenueAccountId.Create(VenueId.Parse("projectx"), "9001");
    private static DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch.AddYears(56);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public ConditionalFiringServiceTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(VenueId.Parse("projectx"));
        A.CallTo(() => _venue.Capabilities).Returns(VenueCapabilities.Of(VenueCapability.BracketOrders));
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
        [
            new VenueAccount(_venueAccount, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice) { Stage = AccountStage.Practice },
        ]);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([]); // flat
        A.CallTo(() => _venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId instrument, CancellationToken _) => Task.FromResult(
                new ResolvedContract(VenueContractId.Create(VenueId.Parse("projectx"), Contract), instrument)));
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Returns(new PlacedOrder(_venueAccount, "889001", DateTimeOffset.UnixEpoch));
    }

    private DbContextOptions<TradingCopilotDbContext> Options =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context() => new(Options, new FixedUser(_operator));

    private ConditionalFiringService Service() => new(
        Context(),
        Options,
        _factory,
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
        Microsoft.Extensions.Options.Options.Create(new ExecutionOptions()),
        new HostTradingEnvironment(DeploymentEnvironment.Development),
        A.Fake<IKillSwitch>(),
        NullLogger<ConditionalFiringService>.Instance);

    private async Task<Guid> SeedAccountAsync()
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
            TrailingAmount = 2_000m,
            PerTradeRiskFraction = 0.15m,
            TargetRewardRatio = 1.5m,
            MaxDrawdownPerTrade = 300m,
            DailyDrawdownGovernor = 600m,
            SizingBasis = SizingBasis.SafetyStop,
            MaxContractsPerOrder = 3,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    private async Task<Guid> AddConditionalAsync(
        Guid accountId,
        ConditionalCrossDirection direction = ConditionalCrossDirection.RisesTo,
        decimal trigger = 5310m,
        decimal safetyStop = 5290m,
        int size = 1,
        decimal? cancelDrift = null,
        DateTimeOffset? expiresAt = null,
        ConditionalStatus status = ConditionalStatus.Pending)
    {
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.ConditionalOrders.Add(new ConditionalOrderRecord
        {
            Id = id,
            UserId = _operator,
            AccountId = accountId,
            Instrument = Contract,
            Symbol = "MES",
            Side = OrderSide.Buy,
            Size = size,
            Type = OrderType.Market,
            EntryPrice = 5300m,
            WorkingStopPrice = 5295m,
            SafetyStopPrice = safetyStop,
            ReferencePrice = 5300m,
            TickSize = 0.25m,
            PointValue = 5m,
            TriggerPrice = trigger,
            TriggerDirection = direction,
            CancelDriftPrice = cancelDrift,
            ExpiresAt = expiresAt,
            Status = status,
            Mode = TradingMode.Practice,
            CreatedAt = Now,
        });
        await context.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ProcessQuote_ShouldFireThroughTheGate_AndJournalTheOrderAndStopPlan_WhenTheTriggerCrossed()
    {
        Guid accountId = await SeedAccountAsync();
        Guid conditionalId = await AddConditionalAsync(accountId); // RisesTo 5310

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        acted.Should().Be(1);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        await using TradingCopilotDbContext reload = Context();
        ConditionalOrderRecord conditional = await reload.ConditionalOrders.SingleAsync(c => c.Id == conditionalId);
        conditional.Status.Should().Be(ConditionalStatus.Fired);
        conditional.FiredOrderId.Should().NotBeNull();

        Order journaled = await reload.Orders.SingleAsync();
        journaled.Id.Should().Be(conditional.FiredOrderId!.Value);
        journaled.Status.Should().Be(OrderStatus.Working);
        journaled.VenueOrderKey.Should().Be("889001");
        journaled.UserId.Should().Be(_operator);                        // ownership preserved on the journaled row
        journaled.EntryMethod.Should().Be(OrderEntryMethod.Conditional); // placed by the on-trigger watcher (gh#181)
        (await reload.StopPlans.CountAsync()).Should().Be(1);           // the fired position is protected (stop staged)
        (await reload.GateDecisions.CountAsync()).Should().Be(1);       // the fire-time decision is audited
    }

    [Fact]
    public async Task ProcessQuote_ShouldNotFire_WhenTheTriggerHasNotCrossed()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId); // RisesTo 5310

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5304m, ask: 5305m, Now, CancellationToken.None);

        acted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Pending);
    }

    [Fact]
    public async Task ProcessQuote_ShouldCancel_OnAnAdverseDriftPastTheBand()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, cancelDrift: 5290m); // RisesTo 5310; stale below 5290

        // Ask 5290 is at the band and below the 5310 trigger: a drift-cancel, not a fire.
        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5289m, ask: 5290m, Now, CancellationToken.None);

        acted.Should().Be(1);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Cancelled);
    }

    [Fact]
    public async Task ProcessQuote_ShouldExpire_WhenTheValidityWindowHasPassed()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, expiresAt: Now.AddMinutes(-1)); // already expired

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5304m, ask: 5305m, Now, CancellationToken.None);

        acted.Should().Be(1);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Expired);
    }

    [Fact]
    public async Task ProcessQuote_ShouldIgnoreResolvedOrders_SoAFiredOneNeverRefires()
    {
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, status: ConditionalStatus.Fired); // already resolved

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5320m, ask: 5321m, Now, CancellationToken.None);

        acted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Fired);
    }

    [Fact]
    public async Task ProcessQuote_ShouldStayPending_WhenTheFireTimeGateRefuses()
    {
        // The trigger crossed, but at fire time the gate refuses (a $500/contract safety stop against a $300
        // budget leaves room for zero) -- the setup is not lost; it re-decides on the next quote. R-12.
        Guid accountId = await SeedAccountAsync();
        await AddConditionalAsync(accountId, safetyStop: 5200m); // 100pt = $500/contract vs the $300 MaxDD

        int acted = await Service().ProcessQuoteAsync(Contract, bid: 5309m, ask: 5310m, Now, CancellationToken.None);

        acted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.SingleAsync()).Status.Should().Be(ConditionalStatus.Pending);
        (await reload.Orders.AnyAsync()).Should().BeFalse();            // nothing transmitted or journaled
        (await reload.GateDecisions.CountAsync()).Should().Be(1);       // the blocking decision is audited
    }
}
