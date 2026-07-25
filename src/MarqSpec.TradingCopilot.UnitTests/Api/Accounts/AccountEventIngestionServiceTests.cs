using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Accounts;

/// <summary>
/// The account-event consumer (gh#219): venue truth becomes journal state. A fill writes a <see cref="Fill"/> row
/// and advances its order; an order-state event moves a working order to a terminal state. The safety-relevant
/// behaviours: fills carry the owner (R-20), an event for a foreign account or unknown order is ignored without
/// throwing, a voided trade is not recorded, and a terminal order is never resurrected.
/// </summary>
public class AccountEventIngestionServiceTests
{
    private const string OurCredentialKey = "topstep-main";
    private static VenueId Projectx { get; } = VenueId.Parse("projectx");
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly DateTimeOffset _now = new(2026, 1, 15, 14, 30, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.Empty)); // background: no request user -> the service must IgnoreQueryFilters to discover

    private AccountEventIngestionService Service() => new(
        Context(),
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = OurCredentialKey }),
        NullLogger<AccountEventIngestionService>.Instance);

    private async Task<Guid> SeedAccountAsync(Guid owner, string venueAccountKey, string credentialKey = OurCredentialKey)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = "Topstep", Type = FirmType.PropFirm });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = owner,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = credentialKey,
        });
        context.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = owner,
            ConnectionId = connectionId,
            VenueAccountKey = venueAccountKey,
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

    private async Task<Guid> SeedOrderAsync(
        Guid owner, Guid accountId, string venueOrderKey, OrderStatus status, int size)
    {
        Guid orderId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Orders.Add(new Order
        {
            Id = orderId,
            UserId = owner,
            AccountId = accountId,
            Instrument = "CON.F.US.MES.U26",
            Side = OrderSide.Buy,
            Size = size,
            Type = OrderType.Market,
            EntryPrice = 5_300m,
            WorkingStopPrice = 5_290m,
            SafetyStopPrice = 5_280m,
            Status = status,
            Mode = TradingMode.Practice,
            VenueOrderKey = venueOrderKey,
            PlacedAt = _now,
        });
        await context.SaveChangesAsync();
        return orderId;
    }

    private FillEvent Fill(string venueAccountKey, string venueOrderKey, string venueFillKey, int quantity, bool voided = false) =>
        new(VenueAccountId.Create(Projectx, venueAccountKey), _now, venueOrderKey, venueFillKey,
            OrderSide.Buy, quantity, new Price(5_300m), Fees: 1.2m, voided);

    private OrderStateEvent OrderState(string venueAccountKey, string venueOrderKey, VenueOrderState state) =>
        new(VenueAccountId.Create(Projectx, venueAccountKey), _now, venueOrderKey, state, FilledQuantity: 0, AverageFillPrice: null);

    private async Task<List<Fill>> FillsAsync() =>
        await Context().Fills.IgnoreQueryFilters().ToListAsync();

    private async Task<Order> OrderAsync(Guid orderId) =>
        await Context().Orders.IgnoreQueryFilters().SingleAsync(order => order.Id == orderId);

    [Fact]
    public async Task ProcessAsync_ShouldPersistFill_AndAdvanceToPartiallyFilled_WhenPartlyFilled()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderAsync(owner, accountId, "55001", OrderStatus.Working, size: 3);

        bool acted = await Service().ProcessAsync(Fill("9001", "55001", "88001", quantity: 1), CancellationToken.None);

        acted.Should().BeTrue();
        List<Fill> fills = await FillsAsync();
        fills.Should().ContainSingle().Which.UserId.Should().Be(owner); // the fill carries its owner (R-20)
        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.PartiallyFilled);
    }

    [Fact]
    public async Task ProcessAsync_ShouldAdvanceToFilled_WhenFillsCompleteTheOrder()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderAsync(owner, accountId, "55001", OrderStatus.Working, size: 3);

        await Service().ProcessAsync(Fill("9001", "55001", "88001", quantity: 1), CancellationToken.None);
        await Service().ProcessAsync(Fill("9001", "55001", "88002", quantity: 2), CancellationToken.None);

        (await FillsAsync()).Should().HaveCount(2);
        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Filled);
    }

    [Fact]
    public async Task ProcessAsync_ShouldPersistFillAndReachFilled_WhenASingleFillCompletesTheOrder()
    {
        // The reviewer's shape: a size-1 order filled by one trade. The fill row and the Filled status must land
        // together (one atomic SaveChanges) -- never a persisted fill with the order stranded at Working.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderAsync(owner, accountId, "55001", OrderStatus.Working, size: 1);

        await Service().ProcessAsync(Fill("9001", "55001", "88001", quantity: 1), CancellationToken.None);

        (await FillsAsync()).Should().ContainSingle();
        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Filled);
    }

    [Fact]
    public async Task ProcessAsync_ShouldMoveWorkingOrderToRejected_OnARejection()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderAsync(owner, accountId, "55001", OrderStatus.Working, size: 3);

        await Service().ProcessAsync(OrderState("9001", "55001", VenueOrderState.Rejected), CancellationToken.None);

        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Rejected); // never left Working after a reject (R-11)
    }

    [Fact]
    public async Task ProcessAsync_ShouldMoveWorkingOrderToCancelled_OnACancellation()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderAsync(owner, accountId, "55001", OrderStatus.Working, size: 3);

        await Service().ProcessAsync(OrderState("9001", "55001", VenueOrderState.Cancelled), CancellationToken.None);

        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task ProcessAsync_ShouldNotResurrectATerminalOrder_OnALateStateEvent()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderAsync(owner, accountId, "55001", OrderStatus.Filled, size: 3);

        bool acted = await Service().ProcessAsync(OrderState("9001", "55001", VenueOrderState.Rejected), CancellationToken.None);

        acted.Should().BeFalse();
        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Filled); // a terminal order is not reopened
    }

    [Fact]
    public async Task ProcessAsync_ShouldSkipAVoidedFill()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid orderId = await SeedOrderAsync(owner, accountId, "55001", OrderStatus.Working, size: 3);

        bool acted = await Service().ProcessAsync(Fill("9001", "55001", "88001", quantity: 1, voided: true), CancellationToken.None);

        acted.Should().BeFalse();
        (await FillsAsync()).Should().BeEmpty(); // a busted trade is not an execution
        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Working);
    }

    [Fact]
    public async Task ProcessAsync_ShouldIgnoreFill_ForAnUnknownOrder()
    {
        Guid owner = Guid.NewGuid();
        await SeedAccountAsync(owner, "9001"); // account exists, but no order with this key

        bool acted = await Service().ProcessAsync(Fill("9001", "does-not-exist", "88001", quantity: 1), CancellationToken.None);

        acted.Should().BeFalse();
        (await FillsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessAsync_ShouldIgnoreEvent_ForAnAccountThisProcessDoesNotServe()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001", credentialKey: "another-firm"); // not ours (ADR-0015)
        Guid orderId = await SeedOrderAsync(owner, accountId, "55001", OrderStatus.Working, size: 3);

        bool acted = await Service().ProcessAsync(Fill("9001", "55001", "88001", quantity: 1), CancellationToken.None);

        acted.Should().BeFalse();
        (await FillsAsync()).Should().BeEmpty();
        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Working); // never touched
    }

    [Fact]
    public async Task ProcessAsync_ShouldNotCrossTheR20Boundary_BetweenOperators()
    {
        Guid operatorA = Guid.NewGuid();
        Guid operatorB = Guid.NewGuid();
        Guid accountA = await SeedAccountAsync(operatorA, "9001");
        Guid accountB = await SeedAccountAsync(operatorB, "9002");
        Guid orderA = await SeedOrderAsync(operatorA, accountA, "55001", OrderStatus.Working, size: 3);
        Guid orderB = await SeedOrderAsync(operatorB, accountB, "77001", OrderStatus.Working, size: 3);

        // A fill on operator B's account.
        await Service().ProcessAsync(Fill("9002", "77001", "88001", quantity: 3), CancellationToken.None);

        Fill fill = (await FillsAsync()).Should().ContainSingle().Which;
        fill.UserId.Should().Be(operatorB);      // owned by B
        fill.OrderId.Should().Be(orderB);
        (await OrderAsync(orderB)).Status.Should().Be(OrderStatus.Filled);
        (await OrderAsync(orderA)).Status.Should().Be(OrderStatus.Working); // A untouched
    }

    [Fact]
    public async Task DiscoverAccountsAsync_ShouldReturnOnlyTheAccountsThisProcessServes()
    {
        Guid owner = Guid.NewGuid();
        await SeedAccountAsync(owner, "9001");
        await SeedAccountAsync(owner, "9002", credentialKey: "another-firm"); // a different credential set

        IReadOnlyList<VenueAccountId> accounts = await Service().DiscoverAccountsAsync(CancellationToken.None);

        accounts.Should().ContainSingle().Which.Should().Be(VenueAccountId.Create(Projectx, "9001"));
    }
}
