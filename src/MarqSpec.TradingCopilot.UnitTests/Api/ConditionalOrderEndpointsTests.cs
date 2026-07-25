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
/// The <c>POST /accounts/{id}/orders/conditional</c> handler (gh#176, ADR-0007) — the second send mode, "send
/// when conditions met". The safety-relevant behaviours: creation <b>transmits nothing</b> (the order is held
/// local until a watcher fires it), the trigger + cancel band are validated before anything is stored, a pre-gate
/// refusal is surfaced now, and the proposal is kept whole for the fire-time re-build (R-12).
/// </summary>
public class ConditionalOrderEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly VenueAccountId _venueAccount = VenueAccountId.Create(VenueId.Parse("projectx"), "9001");

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public ConditionalOrderEndpointsTests()
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
                new ResolvedContract(VenueContractId.Create(VenueId.Parse("projectx"), "CON.F.US.MES.U26"), instrument)));
    }

    private TradingCopilotDbContext Context()
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(_operator));
    }

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static IOptions<ProjectXConnectionOptions> PxOptions() =>
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" });

    private static IOptions<ExecutionOptions> ExecOptions() =>
        Microsoft.Extensions.Options.Options.Create(new ExecutionOptions());

    private static HostTradingEnvironment Development { get; } = new(DeploymentEnvironment.Development);

    private static SendOrderRequest SmallBuy() => new(
        "MES", 0.25m, 5m, OrderSide.Buy, 1, 5300m, 5295m, 5290m, 5300m, OrderType.Market);

    private static CreateConditionalOrderRequest Conditional(
        SendOrderRequest? order = null,
        decimal trigger = 5310m,
        ConditionalCrossDirection direction = ConditionalCrossDirection.RisesTo,
        decimal? cancelDrift = null) =>
        new(order ?? SmallBuy(), trigger, direction, cancelDrift);

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

    private async Task<IResult> CreateAsync(Guid accountId, CreateConditionalOrderRequest request)
    {
        await using TradingCopilotDbContext context = Context();
        return await OrderEndpoints.CreateConditionalOrderAsync(
            accountId, request, new FixedUser(_operator), context, _factory, PxOptions(), ExecOptions(), Development, A.Fake<IKillSwitch>(), CancellationToken.None);
    }

    [Fact]
    public async Task Create_ShouldPersistAPendingConditionalOrder_AndTransmitNothing()
    {
        Guid accountId = await SeedAccountAsync();

        IResult result = await CreateAsync(accountId, Conditional());

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();

        await using TradingCopilotDbContext reload = Context();
        ConditionalOrderRecord record = await reload.ConditionalOrders.SingleAsync();
        record.Status.Should().Be(ConditionalStatus.Pending);       // held local, not at the broker
        record.TriggerPrice.Should().Be(5310m);
        record.TriggerDirection.Should().Be(ConditionalCrossDirection.RisesTo);
        record.Mode.Should().Be(TradingMode.Practice);              // mode stamped at creation (R-14)
        record.FiredOrderId.Should().BeNull();                      // nothing fired yet
        (await reload.Orders.AnyAsync()).Should().BeFalse();        // no order row until it fires
    }

    [Fact]
    public async Task Create_ShouldCarryTheProposalWhole_ForTheFireTimeRebuild()
    {
        // The record must round-trip to the domain proposal a watcher will fire (R-12), take-profit included.
        Guid accountId = await SeedAccountAsync();

        await CreateAsync(accountId, Conditional(order: SmallBuy() with { Target = 5350m }));

        await using TradingCopilotDbContext reload = Context();
        ConditionalOrderRecord record = await reload.ConditionalOrders.SingleAsync();
        record.EntryPrice.Should().Be(5300m);
        record.WorkingStopPrice.Should().Be(5295m);
        record.SafetyStopPrice.Should().Be(5290m);
        record.TakeProfitPrice.Should().Be(5350m);
        record.Symbol.Should().Be("MES");
        record.ToConditionalOrder().ShouldFire(new Price(5310m)).Should().BeTrue(); // rebuilds and fires at the trigger
    }

    [Fact]
    public async Task Create_ShouldRefuseAnUnknownTriggerDirection_AndStoreNothing()
    {
        Guid accountId = await SeedAccountAsync();

        IResult result = await CreateAsync(accountId, Conditional(direction: ConditionalCrossDirection.Unknown));

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest); // the domain aggregate refuses it
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldRefuseACancelBandOnTheWrongSideOfTheTrigger_AndStoreNothing()
    {
        // A RisesTo order's cancel band must sit BELOW its 5310 trigger; 5320 is on the near side -> refused.
        Guid accountId = await SeedAccountAsync();

        IResult result = await CreateAsync(accountId, Conditional(cancelDrift: 5320m));

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldRefuseAPreGateProposal_AndStoreNothing()
    {
        // A wrong-side take-profit is a pre-gate refusal (gh#170) -- surfaced now, nothing coherent to hold.
        Guid accountId = await SeedAccountAsync();

        IResult result = await CreateAsync(accountId, Conditional(order: SmallBuy() with { Target = 5290m }));

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ShouldRefuse_WithoutADeclaredRiskProfile()
    {
        // Fail-closed: the same compose-ladder preconditions as arm/send (gh#10).
        Guid accountId = await SeedAccountAsync();
        await using (TradingCopilotDbContext strip = Context())
        {
            strip.RiskProfiles.RemoveRange(await strip.RiskProfiles.ToListAsync());
            await strip.SaveChangesAsync();
        }

        IResult result = await CreateAsync(accountId, Conditional());

        StatusOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.AnyAsync()).Should().BeFalse();
    }
}
