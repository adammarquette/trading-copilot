using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Recovery;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The startup decision-state rehydration pass (gh#221, ADR-0013): it reads the decision surface back across all
/// owners (no request user, so the R-20 filter is bypassed) and — on an <b>impossible combination</b> a crash left
/// — fails safe to <b>no-new-orders</b> (the kill switch, HaltOnly) and loud, <b>never repairing</b> the row and
/// never downgrading an existing operator lock. A coherent surface engages nothing.
/// </summary>
public class DecisionStateRehydratorTests
{
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly KillSwitch _killSwitch = new(NullExecutionMetrics.Instance);
    private readonly Guid _owner = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    // Startup has no request user, so the context's current user is empty -- the rehydrator must IgnoreQueryFilters
    // to see anything. Building it this way makes the cross-owner discovery a real property under test.
    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.Empty));

    private DecisionStateRehydrator Rehydrator() =>
        new(Context(), _killSwitch, NullLogger<DecisionStateRehydrator>.Instance);

    private Order NewOrder(Guid owner, OrderStatus status, string? venueKey = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        AccountId = Guid.NewGuid(),
        Instrument = "CON.F.US.MES.U26",
        Side = OrderSide.Buy,
        Size = 1,
        Type = OrderType.Market,
        EntryPrice = 5_000m,
        WorkingStopPrice = 4_990m,
        SafetyStopPrice = 4_980m,
        Status = status,
        Mode = TradingMode.Practice,
        VenueOrderKey = venueKey,
        PlacedAt = _now,
    };

    private ConditionalOrderRecord NewConditional(Guid owner, ConditionalStatus status, Guid? firedOrderId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        AccountId = Guid.NewGuid(),
        Instrument = "CON.F.US.MES.U26",
        Side = OrderSide.Buy,
        Size = 1,
        Type = OrderType.Market,
        TriggerPrice = 5_000m,
        TriggerDirection = ConditionalCrossDirection.RisesTo,
        Status = status,
        Mode = TradingMode.Practice,
        FiredOrderId = firedOrderId,
        CreatedAt = _now,
    };

    private StopPlanRecord NewStopPlan(Guid owner, Guid orderId, StopStaging staging) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        OrderId = orderId,
        Side = OrderSide.Buy,
        EntryPrice = 5_000m,
        ActualStopPrice = 4_990m,
        SafetyStopPrice = 4_980m,
        ProximityMetric = StopProximityMetric.Ticks,
        ProximityValue = 4m,
        Staging = staging,
    };

    private async Task SeedAsync(params object[] entities)
    {
        await using TradingCopilotDbContext context = Context();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task RehydrateAsync_ShouldReportConsistent_AndNotEngage_WhenTheSurfaceIsInert()
    {
        Order working = NewOrder(_owner, OrderStatus.Working, venueKey: "PX-1");
        Order staged = NewOrder(_owner, OrderStatus.Staged);
        await SeedAsync(
            working,
            staged,
            NewStopPlan(_owner, staged.Id, StopStaging.Hidden),
            NewStopPlan(_owner, working.Id, StopStaging.Native),
            NewConditional(_owner, ConditionalStatus.Pending),
            NewConditional(_owner, ConditionalStatus.Fired, firedOrderId: working.Id));

        DecisionSurfaceReport report = await Rehydrator().RehydrateAsync(_now, 0, CancellationToken.None);

        report.IsConsistent.Should().BeTrue();
        report.StagedOrders.Should().Be(1);
        report.PendingConditionals.Should().Be(1);
        _killSwitch.IsEngaged.Should().BeFalse(); // an inert surface locks nothing
    }

    [Fact]
    public async Task RehydrateAsync_ShouldCarryTheRecoveryExpiryCount_IntoTheReport_WithoutTrippingTheFailSafe()
    {
        // gh#545: the recovery expire pass runs (in StartupTasks) BEFORE the rehydrator counts, and its count is
        // carried into the report + log. A suggestion carries no risk, so an expired one is a no-risk maintenance
        // count — it must never enter the consistency verdict or engage the kill switch.
        DecisionSurfaceReport report =
            await Rehydrator().RehydrateAsync(_now, expiredOnRecovery: 3, CancellationToken.None);

        report.ExpiredOnRecovery.Should().Be(3);
        report.IsConsistent.Should().BeTrue();
        _killSwitch.IsEngaged.Should().BeFalse();
    }

    [Fact]
    public async Task RehydrateAsync_ShouldEngageHaltOnly_AndPersistTheLock_WhenAnImpossibleCombinationExists()
    {
        await SeedAsync(NewOrder(_owner, OrderStatus.Staged, venueKey: "PX-STALE")); // staged, yet carries a venue key

        DecisionSurfaceReport report = await Rehydrator().RehydrateAsync(_now, 0, CancellationToken.None);

        report.IsConsistent.Should().BeFalse();
        _killSwitch.IsEngaged.Should().BeTrue();
        _killSwitch.Status.Mode.Should().Be(KillSwitchMode.HaltOnly); // no-new-orders; positions stay on native stops

        await using TradingCopilotDbContext context = Context();
        KillSwitchState? lockRow = await context.KillSwitchStates.FirstOrDefaultAsync();
        lockRow.Should().NotBeNull(); // durable -- survives the next restart too
        lockRow!.Engaged.Should().BeTrue();
        lockRow.Mode.Should().Be(KillSwitchMode.HaltOnly);
    }

    [Fact]
    public async Task RehydrateAsync_ShouldNotRepairTheOffendingRow_WhenInconsistent()
    {
        Order stagedAtVenue = NewOrder(_owner, OrderStatus.Staged, venueKey: "PX-STALE");
        await SeedAsync(stagedAtVenue);

        await Rehydrator().RehydrateAsync(_now, 0, CancellationToken.None);

        await using TradingCopilotDbContext context = Context();
        Order reloaded = await context.Orders.IgnoreQueryFilters().SingleAsync(order => order.Id == stagedAtVenue.Id);
        reloaded.Status.Should().Be(OrderStatus.Staged); // untouched -- fail safe, never silently repair
        reloaded.VenueOrderKey.Should().Be("PX-STALE");
    }

    [Fact]
    public async Task RehydrateAsync_ShouldDiscoverAcrossOwners_AndPreserveOwnership()
    {
        Guid ownerB = Guid.NewGuid();
        await SeedAsync(
            NewOrder(_owner, OrderStatus.Working, venueKey: "PX-A"),        // owner A -- coherent
            NewOrder(ownerB, OrderStatus.Staged, venueKey: "PX-B-STALE"));  // owner B -- impossible

        DecisionSurfaceReport report = await Rehydrator().RehydrateAsync(_now, 0, CancellationToken.None);

        report.Orders.Should().Be(2); // both owners discovered, though the context carries no request user
        DecisionInconsistency issue = report.Inconsistencies.Should().ContainSingle().Which;
        issue.Kind.Should().Be(DecisionInconsistencyKind.StagedOrderAtVenue);
        issue.Owner.Should().Be(ownerB); // ownership carried through, never dropped (R-20)
        _killSwitch.IsEngaged.Should().BeTrue();
    }

    [Fact]
    public async Task RehydrateAsync_ShouldPreserveAnExistingLock_WhenTheKillSwitchIsAlreadyEngaged()
    {
        _killSwitch.Engage(KillSwitchMode.FlattenAll, _now, "operator engaged before restart");
        await SeedAsync(NewOrder(_owner, OrderStatus.Staged, venueKey: "PX-STALE"));

        await Rehydrator().RehydrateAsync(_now, 0, CancellationToken.None);

        _killSwitch.IsEngaged.Should().BeTrue();
        _killSwitch.Status.Mode.Should().Be(KillSwitchMode.FlattenAll); // NOT downgraded to HaltOnly
        _killSwitch.Status.Reason.Should().Be("operator engaged before restart"); // the operator's lock stands
    }
}
