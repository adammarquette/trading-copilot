using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Observability;
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
/// The redundant auto-flatten watchdog (gh#187, R-13, ADR-0013): the independent second tier above the primary
/// scheduler (gh#185). It fires only on the primary's <i>failure</i> — a position still open past its deadline
/// plus a grace window — and then <b>persists</b> rather than giving up, respecting a deliberately-disabled market
/// and escalating to a critical alarm past the firing window. The safety property it guards: the flatten still
/// happens when the primary tier is degraded, and a rejected close is never a silent give-up.
/// </summary>
public class AutoFlattenWatchdogServiceTests
{
    private static TimeSpan Grace => TimeSpan.FromMinutes(2);

    private static VenueId Projectx => VenueId.Parse("projectx");

    private static VenueAccountId Account => VenueAccountId.Create(Projectx, "9001");

    // Summer date -> CDT (UTC-5), so 14:30 CT == 19:30 UTC. Off a DST boundary; the DST maths is #69's concern.
    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 7, 15, hour, minute, 0, TimeSpan.Zero);

    private readonly IEventLog _log = A.Fake<IEventLog>();

    public AutoFlattenWatchdogServiceTests()
    {
        A.CallTo(() => _log.AppendAsync(A<EventDraft>._, A<CancellationToken>._))
            .ReturnsLazily((EventDraft d, CancellationToken _) => Task.FromResult(
                new EventEnvelope(d.Id ?? Guid.NewGuid(), 1, d.Type, d.Source, d.OccurredAt, d.OccurredAt, d.Payload, d.TraceParent)));
    }

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private static TradingCopilotDbContext Db(string name, Guid user) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(name).Options, new FixedUser(user));

    private AutoFlattenWatchdogService Service(
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
            _enlister,
            _metrics,
            NullLogger<AutoFlattenWatchdogService>.Instance);

    private readonly INotificationChannel _notifications = A.Fake<INotificationChannel>();

    // The strong path (gh#455): the watchdog's pages ride its own transaction, like the primary tier's.
    private readonly INotificationEnlister _enlister = A.Fake<INotificationEnlister>();

    // Real sink: no behaviour to stub, and recording must be exercised (gh#232).
    private readonly ExecutionMetrics _metrics = new();

    private static FlattenSchedule Schedule(string symbol, TimeOnly close, bool enabled = true) =>
        FlattenSchedule.Create(InstrumentId.Parse(symbol), enabled, deadlineOverride: null, sessionClose: close);

    private static string FrontMonth(string symbol) => symbol switch
    {
        "ES" => "CON.F.US.EP.U26",
        "GC" => "CON.F.US.GC.Q26",
        _ => $"CON.F.US.{symbol}.U26",
    };

    private static PositionSnapshot Pos(string contractKey, int net) =>
        new(Account, VenueContractId.Create(Projectx, contractKey), net, new Price(5_000m));

    private static ITradingVenue Venue(
        IReadOnlyList<PositionSnapshot> positions,
        Func<VenueContractId, PositionSnapshot>? onClose = null,
        Exception? closeThrows = null)
    {
        ITradingVenue venue = A.Fake<ITradingVenue>();
        A.CallTo(() => venue.Id).Returns(Projectx);
        A.CallTo(() => venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).Returns(positions);
        A.CallTo(() => venue.ResolveContractAsync(A<InstrumentId>._, A<CancellationToken>._))
            .ReturnsLazily((InstrumentId i, CancellationToken _) =>
                Task.FromResult(new ResolvedContract(VenueContractId.Create(Projectx, FrontMonth(i.Symbol)), i)));
        if (closeThrows is not null)
        {
            A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
                .Throws(closeThrows);
        }
        else
        {
            A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
                .ReturnsLazily((VenueAccountId a, VenueContractId c, CancellationToken _) =>
                    Task.FromResult(onClose?.Invoke(c) ?? new PositionSnapshot(a, c, 0, new Price(0m))));
        }

        return venue;
    }

    private void AssertAppended(string eventType) =>
        A.CallTo(() => _log.AppendAsync(A<EventDraft>.That.Matches(d => d.Type == eventType), A<CancellationToken>._))
            .MustHaveHappened();

    private void AssertNotAppended(string eventType) =>
        A.CallTo(() => _log.AppendAsync(A<EventDraft>.That.Matches(d => d.Type == eventType), A<CancellationToken>._))
            .MustNotHaveHappened();

    // --- Fires only on the primary's failure: past deadline + grace ---

    [Fact]
    public async Task BackstopAccountAsync_ShouldClose_WhenAPositionIsOpenPastTheDeadlinePlusGrace()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        // 14:33 CT -- three minutes past the 14:30 deadline, beyond the two-minute grace: the primary should have
        // closed this and did not, so the watchdog steps in.
        int saved = await Service().BackstopAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 33), Grace, CancellationToken.None);

        saved.Should().Be(1);
        A.CallTo(() => venue.ClosePositionAsync(
                Account, VenueContractId.Create(Projectx, "CON.F.US.EP.M25"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        AssertAppended(AutoFlattenWatchdogService.SavedEventType);
    }

    [Fact]
    public async Task BackstopAccountAsync_ShouldDeferToThePrimary_WithinTheGraceWindow()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        // 14:31 CT -- one minute past the deadline, still inside the two-minute grace: the primary owns this
        // window; the watchdog must NOT fight it.
        int saved = await Service().BackstopAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 31), Grace, CancellationToken.None);

        saved.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        AssertNotAppended(AutoFlattenWatchdogService.SavedEventType);
    }

    [Fact]
    public async Task BackstopAccountAsync_ShouldDoNothing_BeforeTheDeadline()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        int saved = await Service().BackstopAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(17, 0), Grace, CancellationToken.None);

        saved.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- Respects a deliberately-disabled market (the operator's warned, own-risk override) ---

    [Fact]
    public async Task BackstopAccountAsync_ShouldRespectADisabledMarket_EvenPastTheDeadline()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        // Disabled is the operator's deliberate choice to carry the market past close at their own risk. The
        // watchdog backstops the primary's failures -- it does not override an explicit disable.
        int saved = await Service().BackstopAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30), enabled: false)], Utc(19, 33), Grace, CancellationToken.None);

        saved.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- Persist, never give up: a rejected close is caught, journaled, and retried next pass ---

    [Fact]
    public async Task BackstopAccountAsync_ShouldPersistNotGiveUp_WhenTheCloseIsRejected()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)], closeThrows: new InvalidOperationException("venue rejected the close"));

        // A rejected close must not throw out of the pass (that would stall the watchdog) and must not be silent:
        // journal it and let the next pass try again.
        Func<Task> act = () => Service().BackstopAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 33), Grace, CancellationToken.None);

        await act.Should().NotThrowAsync();
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustHaveHappened();
        AssertAppended(AutoFlattenWatchdogService.RejectedEventType);
        AssertNotAppended(AutoFlattenWatchdogService.SavedEventType);
    }

    [Fact]
    public async Task BackstopAccountAsync_ShouldJournalRejected_WhenTheVenueStillReportsExposureAfterTheClose()
    {
        // The close "succeeded" but the venue still shows the position open (a partial fill / silent reject shape):
        // not saved, and never silent -- journal it and persist.
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)], onClose: c => new PositionSnapshot(Account, c, 2, new Price(5_000m)));

        int saved = await Service().BackstopAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 33), Grace, CancellationToken.None);

        saved.Should().Be(0);
        AssertAppended(AutoFlattenWatchdogService.RejectedEventType);
    }

    // --- Past the firing window: escalate critically, never fire blind ---

    [Fact]
    public async Task BackstopAccountAsync_ShouldEnlistThePageBeforeJournalling_WhenBothTiersHaveFailed()
    {
        // The watchdog is the LAST tier — if its page is the one that goes missing there is nothing behind it. Same
        // atomicity as the primary (gh#455): the page joins this pass's transaction and the journal's save commits
        // both, so a crash cannot leave "both tiers failed" recorded with nobody told.
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        await Service().BackstopAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(20, 35), Grace, CancellationToken.None);

        A.CallTo(() => _enlister.EnlistAsync(
                A<Notification>.That.Matches(n => n.Severity == NotificationSeverity.Page), A<CancellationToken>._))
            .MustHaveHappened()
            .Then(A.CallTo(() => _log.AppendAsync(
                    A<EventDraft>.That.Matches(d => d.Type == AutoFlattenWatchdogService.CriticalEventType),
                    A<CancellationToken>._))
                .MustHaveHappened());

        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task BackstopAccountAsync_ShouldRaiseCritical_PastTheFiringWindowWithExposure()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        // 15:35 CT -- sixty-five minutes past 14:30, beyond the one-hour firing window. Firing blind into the
        // settlement window is worse than escalating: raise the loudest alarm, do not close.
        int saved = await Service().BackstopAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(20, 35), Grace, CancellationToken.None);

        saved.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        AssertAppended(AutoFlattenWatchdogService.CriticalEventType);
    }

    // --- Only ever reduces exposure ---

    [Fact]
    public async Task BackstopAccountAsync_ShouldOnlyReduceExposure_NeverPlacingAnOpeningOrder()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        await Service().BackstopAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 33), Grace, CancellationToken.None);

        A.CallTo(() => venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task BackstopAccountAsync_ShouldJournalSaved_WithTheAccountAndContract()
    {
        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);

        await Service().BackstopAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 33), Grace, CancellationToken.None);

        A.CallTo(() => _log.AppendAsync(
                A<EventDraft>.That.Matches(d =>
                    d.Type == AutoFlattenWatchdogService.SavedEventType
                    && d.Source == AutoFlattenWatchdogService.EventSource
                    && d.Payload.Contains("CON.F.US.EP.M25")
                    && d.Payload.Contains("9001")),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task BackstopAccountAsync_ShouldDoNothing_WhenNoPositionIsOpen()
    {
        ITradingVenue venue = Venue([]);

        int saved = await Service().BackstopAccountAsync(
            Account, venue, [Schedule("ES", new TimeOnly(14, 30))], Utc(19, 33), Grace, CancellationToken.None);

        saved.Should().Be(0);
        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- Enumeration: only this process's accounts (ADR-0015), same set as the primary ---

    [Fact]
    public async Task RunPassAsync_ShouldBackstopAnAccountOfThisProcess_PastTheDeadlinePlusGrace()
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
        await Service(Db(name, Guid.Empty), factory, options).RunPassAsync(Utc(19, 33), CancellationToken.None);

        A.CallTo(() => venue.ClosePositionAsync(
                Account, VenueContractId.Create(Projectx, "CON.F.US.EP.M25"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RunPassAsync_ShouldSkipAnAccount_WhoseConnectionKeyThisProcessDoesNotHold()
    {
        Guid op = Guid.NewGuid();
        string name = Guid.NewGuid().ToString();
        await SeedAccountAsync(name, op, credentialKey: "another-firm");

        ITradingVenue venue = Venue([Pos("CON.F.US.EP.M25", 2)]);
        IProjectXVenueFactory factory = A.Fake<IProjectXVenueFactory>();
        A.CallTo(() => factory.Create(A<FirmConventions>._)).Returns(venue);

        FlattenOptions options = new() { Instruments = [new FlattenScheduleOption { Symbol = "ES", SessionClose = "14:30" }] };
        await Service(Db(name, Guid.Empty), factory, options, credentialKey: "topstep-main")
            .RunPassAsync(Utc(19, 33), CancellationToken.None);

        A.CallTo(() => venue.ClosePositionAsync(A<VenueAccountId>._, A<VenueContractId>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    private static async Task SeedAccountAsync(string database, Guid op, string credentialKey)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();

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
