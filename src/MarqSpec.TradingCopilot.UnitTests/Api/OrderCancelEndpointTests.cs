using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api;

/// <summary>
/// Cancelling a <b>working</b> order via the order API (gh#250): the venue-facing sibling of the staged-ticket
/// discard. The safety-relevant behaviours: only a working order (with a venue handle) reaches the venue; the
/// order's now-orphaned stop plan is retired (else the promotion watcher would place a stop for a cancelled entry,
/// gh#183); a venue rejection never forces a wrong terminal status (the account-event stream reconciles it); and
/// R-20 + the one-credential-set guard hold.
/// </summary>
public class OrderCancelEndpointTests
{
    private const string OurCredentialKey = "topstep-main";
    private static VenueId Projectx { get; } = VenueId.Parse("projectx");
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _operator = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 1, 15, 14, 30, 0, TimeSpan.Zero);
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly IAuditLog _auditLog = A.Fake<IAuditLog>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public OrderCancelEndpointTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(Projectx);
    }

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? _operator));

    private static Microsoft.Extensions.Options.IOptions<ProjectXConnectionOptions> PxOptions() =>
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = OurCredentialKey });

    private Task<IResult> CancelAsync(Guid orderId, Guid? asUser = null) =>
        OrderEndpoints.CancelOrderAsync(
            orderId, Context(asUser), _factory, PxOptions(), _auditLog, NullLoggerFactory.Instance, CancellationToken.None);

    private static int StatusOf(IResult result) => result is IStatusCodeHttpResult status ? status.StatusCode ?? 0 : 0;

    private async Task<Guid> SeedOrderAsync(
        OrderStatus status,
        string? venueOrderKey,
        StopStaging? staging = null,
        Guid? owner = null,
        string credentialKey = OurCredentialKey)
    {
        Guid user = owner ?? _operator;
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(user);
        context.Firms.Add(new Firm { Id = firmId, UserId = user, Name = "Topstep", Type = FirmType.PropFirm });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = user,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = credentialKey,
        });
        context.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = user,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
            CanTrade = true,
            IsVisible = true,
            Balance = 50_000m,
        });
        context.Orders.Add(new Order
        {
            Id = orderId,
            UserId = user,
            AccountId = accountId,
            Instrument = "CON.F.US.MES.U26",
            Side = OrderSide.Buy,
            Size = 1,
            Type = OrderType.Limit,
            EntryPrice = 5_300m,
            WorkingStopPrice = 5_290m,
            SafetyStopPrice = 5_280m,
            Status = status,
            Mode = TradingMode.Practice,
            VenueOrderKey = venueOrderKey,
            PlacedAt = _now,
        });
        if (staging is { } stopStaging)
        {
            context.StopPlans.Add(new StopPlanRecord
            {
                Id = Guid.NewGuid(),
                UserId = user,
                OrderId = orderId,
                Side = OrderSide.Buy,
                EntryPrice = 5_300m,
                ActualStopPrice = 5_290m,
                SafetyStopPrice = 5_280m,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 4m,
                Staging = stopStaging,
            });
        }

        await context.SaveChangesAsync();
        return orderId;
    }

    private async Task<Order> OrderAsync(Guid orderId) =>
        await Context().Orders.IgnoreQueryFilters().SingleAsync(order => order.Id == orderId);

    private async Task<StopStaging?> StagingAsync(Guid orderId) =>
        (await Context().StopPlans.IgnoreQueryFilters().FirstOrDefaultAsync(plan => plan.OrderId == orderId))?.Staging;

    [Fact]
    public async Task CancelOrderAsync_ShouldCancelAWorkingOrderAtTheVenue_RetireItsStopPlan_AndAudit()
    {
        Guid orderId = await SeedOrderAsync(OrderStatus.Working, "PX-55001", StopStaging.Hidden);

        IResult result = await CancelAsync(orderId);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Cancelled);
        (await StagingAsync(orderId)).Should().Be(StopStaging.Retired); // the orphaned plan is retired (gh#183 hazard)
        A.CallTo(() => _venue.CancelOrderAsync(
            A<VenueAccountId>.That.Matches(account => account.Key == "9001"), "PX-55001", A<CancellationToken>._))
            .MustHaveHappened();
        A.CallTo(() => _auditLog.WriteAsync(
            A<IReadOnlyCollection<AuditRecord>>.That.Matches(rows => rows.Any(record =>
                record.Action == AuditAction.OrderCancelled && !record.SyntheticRisk && record.UserId == _operator)),
            A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task CancelOrderAsync_ShouldRefuseANonWorkingNonStagedOrder()
    {
        Guid orderId = await SeedOrderAsync(OrderStatus.Filled, "PX-55001");

        IResult result = await CancelAsync(orderId);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task CancelOrderAsync_ShouldReturnNotFound_ForAnotherOperatorsOrder()
    {
        Guid otherOperator = Guid.NewGuid();
        Guid orderId = await SeedOrderAsync(OrderStatus.Working, "PX-55001", StopStaging.Hidden, owner: otherOperator);

        IResult result = await CancelAsync(orderId); // called as _operator (default) -- B's order is R-20-invisible

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Working); // untouched
    }

    [Fact]
    public async Task CancelOrderAsync_ShouldNotForceTerminalStatus_WhenTheVenueRejects()
    {
        Guid orderId = await SeedOrderAsync(OrderStatus.Working, "PX-55001", StopStaging.Hidden);
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, "PX-55001", A<CancellationToken>._))
            .Throws(new InvalidOperationException("order already gone")); // it may have FILLED, not cancelled

        IResult result = await CancelAsync(orderId);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Working); // NOT forced -- the seam reconciles it
        (await StagingAsync(orderId)).Should().Be(StopStaging.Hidden);       // plan untouched too
    }

    [Fact]
    public async Task CancelOrderAsync_ShouldRefuse_ForAForeignCredentialSet()
    {
        Guid orderId = await SeedOrderAsync(OrderStatus.Working, "PX-55001", StopStaging.Hidden, credentialKey: "another-firm");

        IResult result = await CancelAsync(orderId);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict); // ADR-0015: not this process's credential set
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Working);
    }

    [Fact]
    public async Task CancelOrderAsync_ShouldStillCancel_WhenTheAuditWriteFails()
    {
        Guid orderId = await SeedOrderAsync(OrderStatus.Working, "PX-55001", StopStaging.Hidden);
        A.CallTo(() => _auditLog.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("audit store down"));

        IResult result = await CancelAsync(orderId); // must not throw

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        (await OrderAsync(orderId)).Status.Should().Be(OrderStatus.Cancelled); // the cancel/retire stand
        (await StagingAsync(orderId)).Should().Be(StopStaging.Retired);
    }

    [Fact]
    public async Task CancelOrderAsync_ShouldRefuse_AWorkingOrderWithNoVenueHandle()
    {
        Guid orderId = await SeedOrderAsync(OrderStatus.Working, venueOrderKey: null);

        IResult result = await CancelAsync(orderId);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }
}
