using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Flatten;

/// <summary>
/// The auto-flatten scheduler's core (gh#185, R-13, ADR-0013): the <b>primary</b> trigger that closes open
/// positions at each instrument's per-market deadline. The safety-relevant behaviours: fire at the deadline but
/// never early; each instrument on its own clock; only ever reduce exposure (never open); verify against the
/// venue and retry, escalating loudly rather than giving up silently; and journal every action. The redundant
/// watchdog and the rejected-order / fallback paths are a separate slice (gh#187); settlement reconcile is gh#193.
/// </summary>
public class AutoFlattenServiceTests
{
    private static VenueId Projectx => VenueId.Parse("projectx");

    private static VenueAccountId Account => VenueAccountId.Create(Projectx, "9001");

    // A summer date -> CDT (UTC-5), so 14:30 CT == 19:30 UTC. Pins the tests off a DST boundary; the DST maths of
    // the deadline itself is #69's FlattenSchedule concern, covered there.
    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 7, 15, hour, minute, 0, TimeSpan.Zero);

    private readonly IEventLog _log = A.Fake<IEventLog>();

    public AutoFlattenServiceTests()
    {
        A.CallTo(() => _log.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .ReturnsLazily((EventDraft d, CancellationToken _) => Task.FromResult(
                new EventEnvelope(d.Id ?? Guid.NewGuid(), 1, d.Type, d.Source, d.OccurredAt, d.OccurredAt, d.Payload, d.TraceParent)));
    }

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private static TradingCopilotDbContext Db(string name, Guid user) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(name).Options, new FixedUser(user));

    private AutoFlattenService Service(
        TradingCopilotDbContext? database = null,
        IProjectXVenueFactory? factory = null,
        FlattenOptions? options = null,
        string credentialKey = "topstep-main") =>
        new(
            database ?? Db(Guid.NewGuid().ToString(), Guid.Empty),
            factory ?? A.Fake<IProjectXVenueFactory>(),
            _log,
            Options.Create(new ProjectXConnectionOptions { CredentialKey = credentialKey }),
            Options.Create(options ?? new FlattenOptions()),
            _notifications,
            NullLogger<AutoFlattenService>.Instance);

    private readonly INotificationChannel _notifications = A.Fake<INotificationChannel>();

    private static FlattenSchedule Schedule(string symbol, TimeOnly close, bool enabled = true) =>
        FlattenSchedule.Create(InstrumentId.Parse(symbol), enabled, deadlineOverride: null, sessionClose: close);

    // The front-month contract each instrument resolves to; a real open position is usually the same product in a
    // possibly-different month, so matching is on the PRODUCT ROOT (F.US.EP), never the full id (gh#163).
    private static string FrontMonth(string symbol) => symbol switch
    {
        "ES" => "CON.F.US.EP.U26",
        "NQ" => "CON.F.US.ENQ.U26",
        "GC" => "CON.F.US.GC.Q26",
        "CL" => "CON.F.US.CL.Q26",
        _ => $"CON.F.US.{symbol}.U26",
    };

    private static PositionSnapshot Pos(string contractKey, int net) =>
        new(Account, VenueContractId.Create(Projectx, contractKey), net, new Price(5_000m));

    private static ITradingVenue Venue(
        IReadOnlyList<PositionSnapshot> positions,
        Func<VenueContractId, PositionSnapshot>? onClose = null)
    {
        ITradingVenue venue = A.Fake<ITradingVenue>();
        A.CallTo(() => venue.Id).Returns(Projectx);
        A.CallTo(() => venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).Returns(positions);
        A.CallTo(() => venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId i, CancellationToken _) =>
                Task.FromResult(new ResolvedContract(VenueContractId.Create(Projectx, FrontMonth(i.Symbol)), i)));
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .ReturnsLazily((VenueAccountId a, VenueContractId c, CancellationToken _) =>
                Task.FromResult(onClose?.Invoke(c) ?? new PositionSnapshot(a, c, 0, new Price(0m))));
        return venue;
    }

    private void AssertAppended(string eventType) =>
        A.CallTo(() => _log.AppendAsync(A<EventDraft>.That.Matches(d => d.Type == eventType), A<CancellationToken>._))
            .MustHaveHappened();

    // --- Reaching the operator (gh#243, ADR-0019) ---

    [Fact]
    public async Task FlattenAccountAsync_ShouldPageTheOperator_WhenTheFlattenEscalates()
    {
        // P1. Paged HERE rather than at the firing window: flatten.missed lands 60 min past the deadline, after a
        // prop venue's own forced flatten would already have closed the position -- and a live brokerage has no
        // backstop at all. This is the last moment the operator can still act.
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)], onClose: c => Pos(c.Key, 2)); // never goes flat

        await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45), maxAttempts: 1, CancellationToken.None);

        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.Severity == NotificationSeverity.Page), A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task FlattenAccountAsync_ShouldResolveTheIncident_WhenTheFlattenSucceeds()
    {
        // Cancels a page still nagging from an earlier pass AND re-arms the key, so a LATER failure today is
        // reported as the new incident it is rather than suppressed as a duplicate.
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45), maxAttempts: 3, CancellationToken.None);

        A.CallTo(() => _notifications.ResolveAsync(A<string>._, A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task FlattenAccountAsync_ShouldNotNotify_WhenNothingIsWrong()
    {
        // A clean session must be silent. A rule that pages on a healthy day is a defect in the rule.
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(17, 0), maxAttempts: 3, CancellationToken.None);

        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task FlattenAccountAsync_ShouldStillFlatten_WhenTheNotificationChannelThrows()
    {
        // Alerting is SECONDARY to closing a position. A channel that is down must never abort the one action the
        // system takes without confirmation -- that would make the reporter the outage.
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("channel exploded"));
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)], onClose: c => Pos(c.Key, 2));

        Func<Task> act = () => Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45), maxAttempts: 1, CancellationToken.None);

        await act.Should().NotThrowAsync();
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task FlattenAccountAsync_ShouldNotBlockOnTheNotification_WhenTheChannelHangs()
    {
        // gh#289 regression guard (found by #246): the escalation page is AWAITED on the flatten hot path, so a slow
        // or hung channel adds its full latency to a flatten -- on the R-13 safety path, against a ~15-minute close
        // margin. The send now runs within a deliberate budget: a hang is abandoned and the pass moves on. Without
        // the fix this never completes (the channel hangs forever) and CompleteWithinAsync fails.
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._))
            .ReturnsLazily((Notification _, CancellationToken token) => HangUntilCancelled(token));
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)], onClose: c => Pos(c.Key, 2)); // never flat -> escalates -> pages
        FlattenOptions budget = new() { NotificationBudgetSeconds = 1 };

        Func<Task> pass = () => Service(options: budget).FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45), maxAttempts: 1, CancellationToken.None);

        await pass.Should().CompleteWithinAsync(
            TimeSpan.FromSeconds(4), "a hung notification channel must not block the flatten pass (gh#289)");

        // The page was still ATTEMPTED -- budgeting the send must not skip the escalation, only bound the wait.
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.Severity == NotificationSeverity.Page), A<CancellationToken>._))
            .MustHaveHappened();
    }

    // A send that blocks until its (budget-linked) token is cancelled — the "hung channel" the real HttpClient models
    // when a connect stalls. It observes the token exactly as PushoverNotificationChannel does.
    private static async Task<bool> HangUntilCancelled(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return true;
    }

    // --- The deadline boundary: fire when due, and only then ---

    [Fact]
    public async Task FlattenAccountAsync_ShouldCloseThePosition_WhenPastTheDeadlineWithExposure()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        int closed = await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45), maxAttempts: 3, CancellationToken.None);

        closed.Should().Be(1);
        A.CallTo(() => venue.ClosePositionAsync(
                Account, VenueContractId.Create(Projectx, "CON.F.US.EP.M25"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        AssertAppended(AutoFlattenService.ExecutedEventType);
    }

    [Fact]
    public async Task FlattenAccountAsync_ShouldNotClose_WhenBeforeTheDeadline()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        // 12:00 CT, two and a half hours before the 14:30 deadline: nothing to do yet.
        int closed = await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(17, 0), maxAttempts: 3, CancellationToken.None);

        closed.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task FlattenAccountAsync_ShouldNotClose_ButFlagIt_WhenTheMarketIsDisabled()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        // Disabled with a live position past the deadline is the dangerous case: it must stay VISIBLE, never a
        // silent no-op (R-13 "cannot be silently disabled").
        int closed = await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30), enabled: false)], Utc(19, 45), maxAttempts: 3, CancellationToken.None);

        closed.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        AssertAppended(AutoFlattenService.DisabledEventType);
    }

    [Fact]
    public async Task FlattenAccountAsync_ShouldFlattenEachInstrumentAtItsOwnDeadline()
    {
        // GC flattens at 12:15, ES at 14:30. At 12:20 CT only GC is due.
        ITradingVenue venue = Venue([Pos("CON.F.US.GC.M25", 1), Pos("CON.F.US.EP.M25", 2)]);

        int closed = await Service().FlattenAccountAsync(
            Account,
            venue,
            [Schedule("GC", new TimeOnly(12, 15)), Schedule("ES", new TimeOnly(14, 30))],
            Utc(17, 20),
            maxAttempts: 3,
            CancellationToken.None);

        closed.Should().Be(1);
        A.CallTo(() => venue.ClosePositionAsync(
                Account, VenueContractId.Create(Projectx, "CON.F.US.GC.M25"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => venue.ClosePositionAsync(
                Account, VenueContractId.Create(Projectx, "CON.F.US.EP.M25"), A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- The "only reduces exposure" invariant ---

    [Fact]
    public async Task FlattenAccountAsync_ShouldOnlyReduceExposure_NeverPlacingAnOpeningOrder()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45), maxAttempts: 3, CancellationToken.None);

        // Auto-flatten closes; it never opens. The single order action taken without confirmation must reduce only.
        A.CallTo(() => venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- Verify against the venue, retry, then escalate loudly ---

    [Fact]
    public async Task FlattenAccountAsync_ShouldRetryThenEscalate_WhenThePositionSurvivesTheClose()
    {
        // The venue keeps reporting the position open after each close (a reject / partial-fill shape): retry up
        // to the cap, then escalate -- a surviving position is exactly what the feature exists to prevent.
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)], onClose: c => new PositionSnapshot(Account, c, 2, new Price(5_000m)));

        int closed = await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45), maxAttempts: 2, CancellationToken.None);

        closed.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
        AssertAppended(AutoFlattenService.EscalatedEventType);
    }

    // --- Journalling: every action is recorded (R-13) ---

    [Fact]
    public async Task FlattenAccountAsync_ShouldJournalTheFlatten_WithTheAccountAndContract()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45), maxAttempts: 3, CancellationToken.None);

        A.CallTo(() => _log.AppendAsync(
                A<EventDraft>.That.Matches(d =>
                    d.Type == AutoFlattenService.ExecutedEventType
                    && d.Source == AutoFlattenService.EventSource
                    && d.Payload.Contains("CON.F.US.EP.M25")
                    && d.Payload.Contains("9001")),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    // --- Escalating warnings precede the flatten (R-13) ---

    [Fact]
    public async Task FlattenAccountAsync_ShouldWarn_WhenTheDeadlineIsNear()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        // 14:15 CT -- fifteen minutes before the 14:30 deadline: warn, do not yet close.
        int closed = await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 15), maxAttempts: 3, CancellationToken.None);

        closed.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        AssertAppended(AutoFlattenService.WarningEventType);
    }

    // --- The degraded case: the window passed with exposure still on ---

    [Fact]
    public async Task FlattenAccountAsync_ShouldEscalate_WhenTheFiringWindowPassedWithExposure()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        // 16:00 CT -- ninety minutes past 14:30, beyond the one-hour firing window. Firing blind into the
        // settlement window is worse than escalating; it must not close, and must not be silent (ADR-0013).
        int closed = await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(21, 0), maxAttempts: 3, CancellationToken.None);

        closed.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        AssertAppended(AutoFlattenService.MissedEventType);
    }

    // --- An open position in an unconfigured product is never silently ignored ---

    [Fact]
    public async Task FlattenAccountAsync_ShouldFlagAnOpenPosition_InAnUnconfiguredProduct()
    {
        // Only ES is configured, but a CL position is open. We do not know CL's deadline, so we cannot flatten it
        // safely -- but leaving it silent is the failure R-13 forbids. Flag it loudly.
        ITradingVenue venue = Venue([Pos("CON.F.US.CL.M25", -1)]);

        int closed = await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45), maxAttempts: 3, CancellationToken.None);

        closed.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        AssertAppended(AutoFlattenService.UnconfiguredEventType);
    }

    [Fact]
    public async Task FlattenAccountAsync_ShouldDoNothing_WhenThereIsNoOpenPosition()
    {
        ITradingVenue venue = Venue([]);

        int closed = await Service().FlattenAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 45), maxAttempts: 3, CancellationToken.None);

        closed.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- Enumeration: only this process's accounts, at their deadline (ADR-0015 credential guard) ---

    [Fact]
    public async Task RunPassAsync_ShouldFlattenAnAccountOfThisProcess_AtItsDeadline()
    {
        Guid op = Guid.NewGuid();
        string name = Guid.NewGuid().ToString();
        await SeedAccountAsync(name, op, credentialKey: "topstep-main");

        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);
        A.CallTo(() => venue.GetAccountsAsync(A<CancellationToken>._)).Returns<IReadOnlyList<VenueAccount>>(
            [new VenueAccount(Account, "PRAC-50K", 50_000m, CanTrade: true, IsVisible: true, TradingMode.Practice)]);
        IProjectXVenueFactory factory = A.Fake<IProjectXVenueFactory>();
        A.CallTo(() => factory.Create(A<FirmConventions>._)).Returns(venue);

        FlattenOptions options = new() { Instruments = [new FlattenScheduleOption { Symbol = "ES", SessionClose = "14:30" }] };
        await Service(Db(name, Guid.Empty), factory, options).RunPassAsync(Utc(19, 45), CancellationToken.None);

        A.CallTo(() => venue.ClosePositionAsync(
                Account, VenueContractId.Create(Projectx, "CON.F.US.EP.M25"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunPassAsync_ShouldSkipAnAccount_WhoseConnectionKeyThisProcessDoesNotHold()
    {
        // One credential set per process (ADR-0015): an account on another key is not ours to act on.
        Guid op = Guid.NewGuid();
        string name = Guid.NewGuid().ToString();
        await SeedAccountAsync(name, op, credentialKey: "another-firm");

        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);
        IProjectXVenueFactory factory = A.Fake<IProjectXVenueFactory>();
        A.CallTo(() => factory.Create(A<FirmConventions>._)).Returns(venue);

        FlattenOptions options = new() { Instruments = [new FlattenScheduleOption { Symbol = "ES", SessionClose = "14:30" }] };
        await Service(Db(name, Guid.Empty), factory, options, credentialKey: "topstep-main")
            .RunPassAsync(Utc(19, 45), CancellationToken.None);

        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RunPassAsync_ShouldDoNothing_WhenNoAccountsExist()
    {
        IProjectXVenueFactory factory = A.Fake<IProjectXVenueFactory>();

        Func<Task> act = () => Service(Db(Guid.NewGuid().ToString(), Guid.Empty), factory)
            .RunPassAsync(Utc(19, 45), CancellationToken.None);

        await act.Should().NotThrowAsync();
        A.CallTo(() => factory.Create(A<FirmConventions>._)).MustNotHaveHappened();
    }

    private static async Task SeedAccountAsync(string database, Guid op, string credentialKey)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();

        // Written owned by the operator; the running host reads them back with no user context (filters ignored).
        await using TradingCopilotDbContext seed = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(database).Options,
            new FixedUser(op));

        seed.Firms.Add(new Firm { Id = firmId, UserId = op, Name = "Topstep", Type = FirmType.PropFirm });
        seed.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = op,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = credentialKey,
        });
        seed.Accounts.Add(new Account
        {
            Id = Guid.NewGuid(),
            UserId = op,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
            CanTrade = true,
            IsVisible = true,
        });
        await seed.SaveChangesAsync();
    }
}
