using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The orphan guard (gh#209, gh#191, ADR-0007/0013): on a venue-connection loss hidden stops orphan; on reconnect
/// they re-arm — but only after <b>per-position re-validation against venue truth</b> (gh#191). The safety-relevant
/// behaviours: a stop whose position is still open re-arms; one whose position closed during the outage is
/// <b>retired</b>, not re-armed; re-validation that cannot reach the venue leaves the stop orphaned rather than
/// re-arming on an unverified assumption; a partial close reconciles the protected quantity; and the guard acts
/// across the R-20 boundary while preserving each stop's owner.
/// </summary>
public class OrphanGuardServiceTests
{
    private static VenueId Projectx => VenueId.Parse("projectx");

    private static VenueAccountId AccountId => VenueAccountId.Create(Projectx, "9001");
    private const string Contract = "CON.F.US.MES.U26";

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IProjectXVenueFactory _factory = A.Fake<IProjectXVenueFactory>();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public OrphanGuardServiceTests()
    {
        A.CallTo(() => _factory.Create(A<FirmConventions>._)).Returns(_venue);
        A.CallTo(() => _venue.Id).Returns(Projectx);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
            [new VenueAccount(AccountId, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice)]);
        // Default: the protected position is still open (a 2-lot long) unless a test says otherwise.
        VenuePositions(Pos(2));
    }

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? Guid.Empty));

    private OrphanGuardService Service(string credentialKey = "topstep-main", IAuditLog? auditLog = null) =>
        new(Context(), _factory, Options.Create(new ProjectXConnectionOptions { CredentialKey = credentialKey }),
            auditLog ?? new AuditLog(Context()), NullLogger<OrphanGuardService>.Instance);

    private static PositionSnapshot Pos(int net) =>
        new(AccountId, VenueContractId.Create(Projectx, Contract), net, new Price(5_000m));

    private void VenuePositions(params PositionSnapshot[] positions) =>
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>(positions);

    private void VenueUnreachable() =>
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable during re-validation"));

    // A bare stop (no order / account) — enough for the OrphanAsync path, which is DB-only. Returns its id.
    private async Task<Guid> SeedStopAsync(StopStaging staging, Guid? owner = null)
    {
        Guid planId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(_operator);
        context.StopPlans.Add(NewPlan(planId, staging, owner ?? _operator));
        await context.SaveChangesAsync();
        return planId;
    }

    // A full orphaned plan with its order + account + connection — what re-arm re-validation needs.
    private async Task<Guid> SeedOrphanedAsync(
        int orderSize = 2, OrderSide side = OrderSide.Buy, string credentialKey = "topstep-main", Guid? owner = null)
    {
        Guid who = owner ?? _operator;
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        Guid planId = Guid.NewGuid();

        await using TradingCopilotDbContext seed = Context(who);
        seed.Firms.Add(new Firm { Id = firmId, UserId = who, Name = "Topstep", Type = FirmType.PropFirm });
        seed.Connections.Add(new Connection { Id = connectionId, UserId = who, FirmId = firmId, Platform = "projectx", CredentialKey = credentialKey });
        seed.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = who,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
            CanTrade = true,
            IsVisible = true,
        });
        seed.Orders.Add(new Order
        {
            Id = orderId,
            UserId = who,
            AccountId = accountId,
            Instrument = Contract,
            Symbol = "MES",
            Side = side,
            Size = orderSize,
            Type = OrderType.Market,
            Status = OrderStatus.Working,
            Mode = TradingMode.Practice,
            VenueOrderKey = "V-1",
            PlacedAt = DateTimeOffset.UnixEpoch,
        });
        seed.StopPlans.Add(NewPlan(planId, StopStaging.Orphaned, who, orderId, side));
        await seed.SaveChangesAsync();
        return planId;
    }

    private static StopPlanRecord NewPlan(Guid id, StopStaging staging, Guid owner, Guid? orderId = null, OrderSide side = OrderSide.Buy) => new()
    {
        Id = id,
        UserId = owner,
        OrderId = orderId ?? Guid.NewGuid(),
        Side = side,
        EntryPrice = 5_000m,
        ActualStopPrice = side == OrderSide.Buy ? 4_995m : 5_005m,
        SafetyStopPrice = side == OrderSide.Buy ? 4_990m : 5_010m,
        ProximityMetric = StopProximityMetric.Ticks,
        ProximityValue = 8,
        Staging = staging,
    };

    private async Task<StopPlanRecord> ReloadAsync(Guid planId)
    {
        await using TradingCopilotDbContext reload = Context();
        return await reload.StopPlans.IgnoreQueryFilters().SingleAsync(plan => plan.Id == planId);
    }

    private async Task<List<StopPlanRecord>> AllStopsAsync()
    {
        await using TradingCopilotDbContext reload = Context();
        return await reload.StopPlans.IgnoreQueryFilters().ToListAsync();
    }

    private async Task<List<AuditRecord>> AllAuditsAsync()
    {
        await using TradingCopilotDbContext reload = Context();
        return await reload.AuditRecords.IgnoreQueryFilters().ToListAsync();
    }

    // Reads through the R-20 default-deny filter as a specific operator (no IgnoreQueryFilters).
    private async Task<List<AuditRecord>> AuditsVisibleToAsync(Guid user)
    {
        await using TradingCopilotDbContext reload = Context(user);
        return await reload.AuditRecords.ToListAsync();
    }

    private static IAuditLog ThrowingAuditLog()
    {
        IAuditLog auditLog = A.Fake<IAuditLog>();
        A.CallTo(() => auditLog.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("audit store unreachable"));
        return auditLog;
    }

    // --- Orphaning (unchanged from gh#209) ---

    [Fact]
    public async Task OrphanAsync_ShouldMoveOnlyHiddenStopsToOrphaned_LeavingNativeUntouched()
    {
        await SeedStopAsync(StopStaging.Hidden);
        await SeedStopAsync(StopStaging.Hidden);
        await SeedStopAsync(StopStaging.Native);

        int orphaned = await Service().OrphanAsync(CancellationToken.None);

        orphaned.Should().Be(2);
        List<StopPlanRecord> stops = await AllStopsAsync();
        stops.Count(s => s.Staging == StopStaging.Orphaned).Should().Be(2);
        stops.Count(s => s.Staging == StopStaging.Native).Should().Be(1);
        stops.Should().NotContain(s => s.Staging == StopStaging.Hidden);
    }

    [Fact]
    public async Task OrphanAsync_ShouldDoNothing_WhenNoHiddenStops()
    {
        await SeedStopAsync(StopStaging.Native);

        int orphaned = await Service().OrphanAsync(CancellationToken.None);

        orphaned.Should().Be(0);
        (await AllStopsAsync()).Should().OnlyContain(s => s.Staging == StopStaging.Native);
    }

    // --- Re-arm re-validation (gh#191) ---

    [Fact]
    public async Task RearmAsync_ShouldReArmToHidden_WhenThePositionIsStillOpen()
    {
        Guid planId = await SeedOrphanedAsync(orderSize: 2, side: OrderSide.Buy);
        VenuePositions(Pos(2)); // still a 2-lot long

        RearmOutcome outcome = await Service().RearmAsync(CancellationToken.None);

        outcome.Rearmed.Should().Be(1);
        (await ReloadAsync(planId)).Staging.Should().Be(StopStaging.Hidden);
    }

    [Fact]
    public async Task RearmAsync_ShouldRetire_WhenThePositionClosedDuringTheOutage()
    {
        Guid planId = await SeedOrphanedAsync(side: OrderSide.Buy);
        VenuePositions(); // the venue reports no open position -- it closed during the outage

        RearmOutcome outcome = await Service().RearmAsync(CancellationToken.None);

        // A stop for a position that no longer exists must NOT re-arm and must NOT promote (ADR-0013).
        outcome.Retired.Should().Be(1);
        outcome.Rearmed.Should().Be(0);
        (await ReloadAsync(planId)).Staging.Should().Be(StopStaging.Retired);
    }

    [Fact]
    public async Task RearmAsync_ShouldRetire_WhenThePositionFlippedDirection()
    {
        Guid planId = await SeedOrphanedAsync(side: OrderSide.Buy);
        VenuePositions(Pos(-2)); // the old long is gone; a short exists now -- the old stop does not apply

        RearmOutcome outcome = await Service().RearmAsync(CancellationToken.None);

        outcome.Retired.Should().Be(1);
        (await ReloadAsync(planId)).Staging.Should().Be(StopStaging.Retired);
    }

    [Fact]
    public async Task RearmAsync_ShouldLeaveOrphaned_WhenTheVenueCannotBeReached()
    {
        Guid planId = await SeedOrphanedAsync();
        VenueUnreachable();

        // Uncertainty resolves to the safe state (engineering §9): never re-arm on an unverified assumption. Do
        // not throw out of the pass -- leave it orphaned and retry next pass.
        Func<Task> act = async () => await Service().RearmAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        RearmOutcome outcome = await Service().RearmAsync(CancellationToken.None);
        outcome.StillOrphaned.Should().BeGreaterThan(0);
        (await ReloadAsync(planId)).Staging.Should().Be(StopStaging.Orphaned);
    }

    [Fact]
    public async Task RearmAsync_ShouldReconcileTheQuantity_WhenThePositionWasPartiallyClosed()
    {
        Guid planId = await SeedOrphanedAsync(orderSize: 2, side: OrderSide.Buy);
        VenuePositions(Pos(1)); // 2 lots became 1 during the outage

        RearmOutcome outcome = await Service().RearmAsync(CancellationToken.None);

        // Re-arm, but protect the quantity that actually remains -- not the original.
        outcome.Rearmed.Should().Be(1);
        (await ReloadAsync(planId)).Staging.Should().Be(StopStaging.Hidden);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Orders.IgnoreQueryFilters().SingleAsync()).Size.Should().Be(1);
    }

    [Fact]
    public async Task RearmAsync_ShouldLeaveOrphaned_ForAnAccountOnAnotherCredentialKey()
    {
        Guid planId = await SeedOrphanedAsync(credentialKey: "another-firm");

        RearmOutcome outcome = await Service(credentialKey: "topstep-main").RearmAsync(CancellationToken.None);

        // One credential set per process (ADR-0015): not ours to re-validate; leave it for the process that serves it.
        outcome.StillOrphaned.Should().BeGreaterThan(0);
        (await ReloadAsync(planId)).Staging.Should().Be(StopStaging.Orphaned);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task RearmAsync_ShouldPreserveOwner_WhenReValidating()
    {
        Guid alice = Guid.NewGuid();
        Guid planId = await SeedOrphanedAsync(owner: alice);
        VenuePositions(Pos(2));

        await Service().RearmAsync(CancellationToken.None);

        (await ReloadAsync(planId)).UserId.Should().Be(alice); // ownership preserved, never re-assigned
    }

    [Fact]
    public async Task RearmAsync_ShouldDoNothing_WhenNoOrphanedStops()
    {
        await SeedStopAsync(StopStaging.Native);

        RearmOutcome outcome = await Service().RearmAsync(CancellationToken.None);

        outcome.Should().Be(new RearmOutcome(0, 0, 0));
    }

    // --- Audit trail (gh#220) ---

    [Fact]
    public async Task OrphanAsync_ShouldWriteOneSyntheticRiskAuditRow_PerOrphanedStop()
    {
        Guid first = await SeedStopAsync(StopStaging.Hidden);
        Guid second = await SeedStopAsync(StopStaging.Hidden);
        Guid native = await SeedStopAsync(StopStaging.Native); // never orphaned, so never audited

        await Service().OrphanAsync(CancellationToken.None);

        List<AuditRecord> audits = await AllAuditsAsync();
        audits.Should().HaveCount(2);
        audits.Should().OnlyContain(a =>
            a.Action == AuditAction.ConnectionLoss
            && a.Placement == AuditPlacement.Synthetic
            && a.SyntheticRisk
            && a.Before == "Hidden"
            && a.After == "Orphaned"
            && a.UserId == _operator);
        audits.Select(a => a.StopPlanId!.Value).Should().BeEquivalentTo(new[] { first, second });
        audits.Should().NotContain(a => a.StopPlanId == native); // the native stop is never in the audit
    }

    [Fact]
    public async Task OrphanAsync_ShouldWriteNoAuditRows_WhenNoHiddenStops()
    {
        await SeedStopAsync(StopStaging.Native);

        await Service().OrphanAsync(CancellationToken.None);

        (await AllAuditsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RearmAsync_ShouldWriteASyntheticRiskAuditRow_WhenReArming()
    {
        Guid planId = await SeedOrphanedAsync(orderSize: 2, side: OrderSide.Buy);
        VenuePositions(Pos(2)); // still open — re-arms

        await Service().RearmAsync(CancellationToken.None);

        AuditRecord audit = (await AllAuditsAsync()).Should().ContainSingle().Subject;
        audit.Before.Should().Be("Orphaned");
        audit.After.Should().Be("Hidden");
        audit.SyntheticRisk.Should().BeTrue();
        audit.Placement.Should().Be(AuditPlacement.Synthetic);
        audit.Action.Should().Be(AuditAction.ConnectionLoss);
        audit.StopPlanId.Should().Be(planId);
    }

    [Fact]
    public async Task RearmAsync_ShouldWriteASyntheticRiskAuditRow_WhenRetiring()
    {
        Guid planId = await SeedOrphanedAsync(side: OrderSide.Buy);
        VenuePositions(); // closed during the outage — retires

        await Service().RearmAsync(CancellationToken.None);

        AuditRecord audit = (await AllAuditsAsync()).Should().ContainSingle().Subject;
        audit.Before.Should().Be("Orphaned");
        audit.After.Should().Be("Retired");
        audit.SyntheticRisk.Should().BeTrue();
        audit.StopPlanId.Should().Be(planId);
    }

    [Fact]
    public async Task RearmAsync_ShouldWriteNoAuditRow_ForAStopLeftOrphaned()
    {
        await SeedOrphanedAsync();
        VenueUnreachable(); // cannot re-validate — the stop stays orphaned, so nothing transitioned to record

        await Service().RearmAsync(CancellationToken.None);

        (await AllAuditsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RearmAsync_ShouldStampTheAuditRowWithTheStopsOwner()
    {
        Guid alice = Guid.NewGuid();
        await SeedOrphanedAsync(owner: alice);
        VenuePositions(Pos(2));

        await Service().RearmAsync(CancellationToken.None);

        // Written from background plumbing with no user context, yet owned by the affected stop's operator (R-20).
        (await AllAuditsAsync()).Should().ContainSingle().Which.UserId.Should().Be(alice);
    }

    [Fact]
    public async Task AuditRows_ShouldBeInvisibleToAnotherOperator()
    {
        Guid alice = Guid.NewGuid();
        await SeedStopAsync(StopStaging.Hidden, owner: alice);

        await Service().OrphanAsync(CancellationToken.None);

        (await AuditsVisibleToAsync(alice)).Should().ContainSingle();     // the owner sees their audit row
        (await AuditsVisibleToAsync(Guid.NewGuid())).Should().BeEmpty();  // another operator sees nothing (R-20)
    }

    [Fact]
    public async Task OrphanAsync_ShouldStillOrphanTheStop_WhenTheAuditWriteThrows()
    {
        Guid planId = await SeedStopAsync(StopStaging.Hidden);
        IAuditLog throwing = ThrowingAuditLog();

        int orphaned = 0;
        Func<Task> act = async () => orphaned = await Service(auditLog: throwing).OrphanAsync(CancellationToken.None);

        // The audit is a secondary write: its failure is swallowed, the safety action still completes (gh#220).
        await act.Should().NotThrowAsync();
        orphaned.Should().Be(1);
        (await ReloadAsync(planId)).Staging.Should().Be(StopStaging.Orphaned);
        A.CallTo(() => throwing.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .MustHaveHappened(); // the guard did attempt the audit — the failure was tolerated, not skipped
    }

    [Fact]
    public async Task RearmAsync_ShouldStillReArm_WhenTheAuditWriteThrows()
    {
        Guid planId = await SeedOrphanedAsync(orderSize: 2, side: OrderSide.Buy);
        VenuePositions(Pos(2));

        // If the guard did not tolerate the audit failure, this call would throw and the test would fail here.
        RearmOutcome outcome = await Service(auditLog: ThrowingAuditLog()).RearmAsync(CancellationToken.None);

        outcome.Rearmed.Should().Be(1);
        (await ReloadAsync(planId)).Staging.Should().Be(StopStaging.Hidden);
    }
}
