using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Audit;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Kill;

/// <summary>
/// The kill switch's engage / disengage core (gh#189, R-11, ADR-0007): engaging disables outbound (flag + durable
/// row), cancels working orders, and either flattens all positions or halts only; disengaging re-enables outbound.
/// The safety properties: outbound is blocked before anything is torn down, halt-only never touches positions, and
/// every transition is persisted and journaled.
/// </summary>
public class KillSwitchServiceTests
{
    private static VenueId Projectx => VenueId.Parse("projectx");
    private static VenueAccountId Account => VenueAccountId.Create(Projectx, "9001");
    private static DateTimeOffset Now => DateTimeOffset.UnixEpoch;

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly KillSwitch _killSwitch = new(NullExecutionMetrics.Instance);
    private readonly IEventLog _log = A.Fake<IEventLog>();
    private readonly IAuditLog _auditLog = A.Fake<IAuditLog>();
    private readonly List<AuditRecord> _audited = [];
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public KillSwitchServiceTests()
    {
        // Capture every immutable audit row the service writes, in order, so the append-only history is assertable.
        A.CallTo(() => _auditLog.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Invokes((IReadOnlyCollection<AuditRecord> records, CancellationToken _) => _audited.AddRange(records));

        A.CallTo(() => _venue.Id).Returns(Projectx);
        A.CallTo(() => _venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
            [new VenueAccount(Account, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice)]);
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Returns<IReadOnlyList<PositionSnapshot>>([]);
        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .ReturnsLazily((VenueAccountId a, VenueContractId c, CancellationToken _) =>
                Task.FromResult(new PositionSnapshot(a, c, 0, new Price(0m))));
        A.CallTo(() => _log.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .ReturnsLazily((EventDraft d, CancellationToken _) => Task.FromResult(
                new EventEnvelope(d.Id ?? Guid.NewGuid(), 1, d.Type, d.Source, d.OccurredAt, d.OccurredAt, d.Payload, d.TraceParent)));
    }

    private TradingCopilotDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options, new FixedUser(_operator));

    private KillSwitchService Service(TradingCopilotDbContext database, string credentialKey = "topstep-main")
    {
        IProjectXVenueFactory factory = A.Fake<IProjectXVenueFactory>();
        A.CallTo(() => factory.Create(A<FirmConventions>._)).Returns(_venue);
        return new KillSwitchService(
            database, factory, _log, _auditLog, new FixedUser(_operator), _killSwitch,
            Options.Create(new ProjectXConnectionOptions { CredentialKey = credentialKey }),
            NullLogger<KillSwitchService>.Instance);
    }

    private async Task SeedAsync(bool withWorkingOrder = false, string credentialKey = "topstep-main")
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await using TradingCopilotDbContext seed = Db();
        seed.Firms.Add(new Firm { Id = firmId, UserId = _operator, Name = "Topstep", Type = FirmType.PropFirm });
        seed.Connections.Add(new Connection { Id = connectionId, UserId = _operator, FirmId = firmId, Platform = "projectx", CredentialKey = credentialKey });
        seed.Accounts.Add(new Account
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
        });
        if (withWorkingOrder)
        {
            seed.Orders.Add(new Order
            {
                Id = Guid.NewGuid(),
                UserId = _operator,
                AccountId = accountId,
                Instrument = "CON.F.US.MES.U26",
                Symbol = "MES",
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Limit,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                VenueOrderKey = "V-777",
                PlacedAt = Now,
            });
        }

        await seed.SaveChangesAsync();
    }

    private void OpenPosition() =>
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).Returns<IReadOnlyList<PositionSnapshot>>(
            [new PositionSnapshot(Account, VenueContractId.Create(Projectx, "CON.F.US.MES.U26"), 2, new Price(5_000m))]);

    // --- Faults on the flatten path (gh#529, P1) ---

    [Fact]
    public async Task EngageAsync_ShouldStillJournalTheEngagement_WhenTheVenueRefusesToClose()
    {
        // ProjectXVenue.ClosePositionAsync THROWS on an ordinary venue refusal, and nothing above this caught it.
        // The throw unwound both loops, so SaveChangesAsync and the killswitch.engaged append were never reached --
        // ADR-0007 says every transition is journaled, and the one transition that matters most was not.
        await SeedAsync(withWorkingOrder: true);
        OpenPosition();
        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue refused the close"));

        Func<Task> engage = () => Service(Db()).EngageAsync(KillSwitchMode.FlattenAll, null, Now, CancellationToken.None);

        await engage.Should().NotThrowAsync("the operator reached for this BECAUSE something is already wrong");
        A.CallTo(() => _log.AppendAsync(
                A<EventDraft>.That.Matches(draft => draft.Type == KillSwitchService.EngagedEventType),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task EngageAsync_ShouldNameTheAccountItCouldNotReach_WhenTheVenueRefuses()
    {
        // Reported, not silently dropped: the accounts the kill switch could NOT complete are exactly the ones the
        // operator now has to handle by hand, so they belong in the journal entry rather than only in a log line.
        //
        // The fault is on the POSITIONS READ, deliberately — a close refusal is now absorbed by the retry loop and
        // escalates instead (see the escalation guard below), so it never reaches the per-account handler. An
        // unreachable venue does, and that is the shape this isolation exists for.
        await SeedAsync();
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable"));

        await Service(Db()).EngageAsync(KillSwitchMode.FlattenAll, null, Now, CancellationToken.None);

        A.CallTo(() => _log.AppendAsync(
                A<EventDraft>.That.Matches(draft =>
                    draft.Type == KillSwitchService.EngagedEventType && draft.Payload.Contains("INCOMPLETE")),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task EngageAsync_ShouldJournalEscalation_WhenThePositionWillNotClose()
    {
        // The verdict that used to be invisible. A venue that keeps reporting the position open produced 200 OK with
        // FlattenedPositions: 0 -- byte-identical to halt-only and to "nothing was open" -- with a LogError as the
        // only trace. An operator could not tell those apart.
        await SeedAsync();
        OpenPosition();
        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .ReturnsLazily((VenueAccountId a, VenueContractId c, CancellationToken _) =>
                Task.FromResult(new PositionSnapshot(a, c, 2, new Price(5_000m)))); // never goes flat

        KillSwitchReport report = await Service(Db()).EngageAsync(
            KillSwitchMode.FlattenAll, null, Now, CancellationToken.None);

        report.FlattenedPositions.Should().Be(0, "nothing actually closed");
        A.CallTo(() => _log.AppendAsync(
                A<EventDraft>.That.Matches(draft => draft.Type == KillSwitchService.EscalatedEventType),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task EngageAsync_ShouldEngageTheRuntimeFlag_SoOutboundIsBlockedImmediately()
    {
        await SeedAsync();

        await Service(Db()).EngageAsync(KillSwitchMode.HaltOnly, reason: "looks wrong", Now, CancellationToken.None);

        _killSwitch.IsEngaged.Should().BeTrue();
    }

    [Fact]
    public async Task EngageAsync_ShouldCancelWorkingOrders_AndMarkThemCancelled()
    {
        await SeedAsync(withWorkingOrder: true);

        KillSwitchReport report = await Service(Db()).EngageAsync(KillSwitchMode.HaltOnly, null, Now, CancellationToken.None);

        report.CancelledOrders.Should().Be(1);
        A.CallTo(() => _venue.CancelOrderAsync(Account, "V-777", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        await using TradingCopilotDbContext reload = Db();
        (await reload.Orders.SingleAsync()).Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task EngageAsync_ShouldFlattenOpenPositions_WhenModeIsFlattenAll()
    {
        await SeedAsync();
        OpenPosition();

        KillSwitchReport report = await Service(Db()).EngageAsync(KillSwitchMode.FlattenAll, null, Now, CancellationToken.None);

        report.FlattenedPositions.Should().Be(1);
        A.CallTo(() => _venue.ClosePositionAsync(Account, VenueContractId.Create(Projectx, "CON.F.US.MES.U26"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task EngageAsync_ShouldLeavePositionsOnTheirStops_WhenModeIsHaltOnly()
    {
        await SeedAsync(withWorkingOrder: true);
        OpenPosition();

        KillSwitchReport report = await Service(Db()).EngageAsync(KillSwitchMode.HaltOnly, null, Now, CancellationToken.None);

        // Halt-only cancels working orders but never touches open positions -- they rest on their native stops.
        report.FlattenedPositions.Should().Be(0);
        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._)).MustNotHaveHappened();
        report.CancelledOrders.Should().Be(1);
    }

    [Fact]
    public async Task EngageAsync_ShouldPersistTheEngagedState_SoItSurvivesARestart()
    {
        await SeedAsync();

        await Service(Db()).EngageAsync(KillSwitchMode.FlattenAll, "abort", Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Db();
        KillSwitchState row = await reload.KillSwitchStates.SingleAsync();
        row.Engaged.Should().BeTrue();
        row.Mode.Should().Be(KillSwitchMode.FlattenAll);
        row.Reason.Should().Be("abort");
    }

    [Fact]
    public async Task EngageAsync_ShouldJournalTheEngagement()
    {
        await SeedAsync();

        await Service(Db()).EngageAsync(KillSwitchMode.FlattenAll, null, Now, CancellationToken.None);

        A.CallTo(() => _log.AppendAsync(
                A<EventDraft>.That.Matches(d => d.Type == KillSwitchService.EngagedEventType && d.Source == KillSwitchService.EventSource),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task EngageAsync_ShouldSkipAccounts_OnAnotherCredentialKey()
    {
        await SeedAsync(withWorkingOrder: true, credentialKey: "another-firm");

        KillSwitchReport report = await Service(Db(), credentialKey: "topstep-main")
            .EngageAsync(KillSwitchMode.FlattenAll, null, Now, CancellationToken.None);

        // One credential set per process (ADR-0015): an account on another key is not ours to cancel or flatten.
        report.CancelledOrders.Should().Be(0);
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task DisengageAsync_ShouldClearTheFlag_PersistDisengaged_AndJournal()
    {
        await SeedAsync();
        KillSwitchService service = Service(Db());
        await service.EngageAsync(KillSwitchMode.FlattenAll, null, Now, CancellationToken.None);

        await service.DisengageAsync(Now, CancellationToken.None);

        _killSwitch.IsEngaged.Should().BeFalse();
        await using TradingCopilotDbContext reload = Db();
        (await reload.KillSwitchStates.SingleAsync()).Engaged.Should().BeFalse();
        A.CallTo(() => _log.AppendAsync(
                A<EventDraft>.That.Matches(d => d.Type == KillSwitchService.DisengagedEventType), A<CancellationToken>._))
            .MustHaveHappened();
    }

    // --- Immutable §9 audit (gh#765) ---

    [Fact]
    public async Task EngageAsync_ShouldWriteAnImmutableAuditRecord_CarryingTheSourceModeAndReason()
    {
        await SeedAsync();

        await Service(Db()).EngageAsync(KillSwitchMode.FlattenAll, "abort", Now, CancellationToken.None);

        AuditRecord record = _audited.Should().ContainSingle().Subject;
        record.Action.Should().Be(AuditAction.KillSwitchEngaged);
        record.Source.Should().Be(AuditSource.Operator, "the operator engaged it — the only trigger wired today");
        record.Placement.Should().Be(AuditPlacement.None, "a kill-switch action rests on no single protective leg");
        record.UserId.Should().Be(_operator, "the audit is owned by the operator who acted (R-20)");
        record.After.Should().Contain("FlattenAll", "the mode belongs in the durable record");
        record.Detail.Should().Contain("abort", "so does the reason");
        record.RecordedAt.Should().Be(Now);
    }

    [Fact]
    public async Task DisengageAsync_ShouldWriteAnImmutableAuditRecord()
    {
        await SeedAsync();
        KillSwitchService service = Service(Db());
        await service.EngageAsync(KillSwitchMode.FlattenAll, null, Now, CancellationToken.None);
        _audited.Clear(); // isolate the disengage's own record from the engage's above

        await service.DisengageAsync(Now, CancellationToken.None);

        AuditRecord record = _audited.Should().ContainSingle().Subject;
        record.Action.Should().Be(AuditAction.KillSwitchDisengaged);
        record.Source.Should().Be(AuditSource.Operator);
        record.After.Should().Be("Disengaged");
    }

    [Fact]
    public async Task EngageDisengageEngage_ShouldWriteThreeImmutableAuditRecordsInOrder()
    {
        // The acceptance criterion (gh#765). Before this the single KillSwitchState row was overwritten in place, so
        // after a second engage the first transition was gone. Three transitions must leave three durable records, in
        // order, each carrying its trigger source — the history the incident review reads.
        await SeedAsync();
        KillSwitchService service = Service(Db());

        await service.EngageAsync(KillSwitchMode.FlattenAll, "first", Now, CancellationToken.None);
        await service.DisengageAsync(Now, CancellationToken.None);
        await service.EngageAsync(KillSwitchMode.HaltOnly, "again", Now, CancellationToken.None);

        _audited.Select(record => record.Action).Should().Equal(
            AuditAction.KillSwitchEngaged, AuditAction.KillSwitchDisengaged, AuditAction.KillSwitchEngaged);
        _audited.Should().OnlyContain(record => record.Source == AuditSource.Operator);
        _audited.Should().OnlyContain(record => record.Placement == AuditPlacement.None);
        // Each engage's own reason rides into its own record (the per-record "reason" half of the acceptance criterion).
        _audited[0].Detail.Should().Contain("first");
        _audited[2].Detail.Should().Contain("again");
    }

    [Fact]
    public async Task EngageAsync_ShouldCarryTheProvidedSource_WhenSomethingOtherThanTheOperatorTripsIt()
    {
        // The Source is a first-class field, not hardcoded to Operator: a future guardrail or dead-man's-switch trip
        // records ITS trigger, so an incident review can tell an operator engage from an automatic one (gh#765).
        await SeedAsync();

        await Service(Db()).EngageAsync(
            KillSwitchMode.FlattenAll, null, Now, CancellationToken.None, AuditSource.Guardrail);

        _audited.Should().ContainSingle().Subject.Source.Should().Be(AuditSource.Guardrail);
    }

    [Fact]
    public async Task EngageAsync_ShouldNotThrowNorLoseTheEngage_WhenTheAuditWriteFails()
    {
        // The audit is a SECONDARY write: a failure loses a history row but must never abort the kill switch the
        // operator reached for (the IAuditLog contract). The engage still flips the flag and journals.
        await SeedAsync();
        A.CallTo(() => _auditLog.WriteAsync(A<IReadOnlyCollection<AuditRecord>>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("audit store down"));

        Func<Task> engage = () => Service(Db()).EngageAsync(KillSwitchMode.FlattenAll, null, Now, CancellationToken.None);

        await engage.Should().NotThrowAsync();
        _killSwitch.IsEngaged.Should().BeTrue("the safety action completed even though its audit row did not");
        A.CallTo(() => _log.AppendAsync(
                A<EventDraft>.That.Matches(d => d.Type == KillSwitchService.EngagedEventType), A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task EngageAsync_ShouldRecordTheEscalation_WhenAFlattenAllCannotConfirmFlat()
    {
        // gh#765 review. A FlattenAll that cannot confirm flat is the worst kill outcome, and it returns normally --
        // so without surfacing it, the audit row is byte-identical to a clean kill that closed nothing. The immutable
        // trail must be reconstructable alone: the escalation belongs in the row, not only the event log.
        await SeedAsync();
        OpenPosition();
        A.CallTo(() => _venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .ReturnsLazily((VenueAccountId a, VenueContractId c, CancellationToken _) =>
                Task.FromResult(new PositionSnapshot(a, c, 2, new Price(5_000m)))); // never goes flat

        await Service(Db()).EngageAsync(KillSwitchMode.FlattenAll, null, Now, CancellationToken.None);

        AuditRecord record = _audited.Should().ContainSingle().Subject;
        record.After.Should().Contain("escalated", "an escalated kill must be distinguishable in the audit table");
        record.Detail.Should().Contain("ESCALATED", "with the still-open count reconstructable from the row alone");
    }

    [Fact]
    public async Task EngageAsync_ShouldBoundTheAuditDetail_ToTheColumnWidth()
    {
        // The operator reason is unbounded, but AuditRecord.Detail is varchar(512); an over-long reason would fail the
        // real-Postgres write (silently, since the audit is failure-tolerant). The writer truncates to fit (gh#765).
        await SeedAsync();
        string hugeReason = new('x', 2_000);

        await Service(Db()).EngageAsync(KillSwitchMode.HaltOnly, hugeReason, Now, CancellationToken.None);

        _audited.Should().ContainSingle().Subject.Detail.Length.Should().BeLessThanOrEqualTo(512);
    }
}
