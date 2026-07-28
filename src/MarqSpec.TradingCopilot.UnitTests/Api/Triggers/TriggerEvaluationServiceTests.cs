using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// The deterministic trigger scan's core (gh#385, ADR-0008): evaluate each enabled mechanical trigger against its
/// resolved indicator value and fire the crossing edges as alerts. The design-defining behaviours: a crossing fires
/// exactly once and then debounces; a null indicator holds state (fail-closed); a trigger created already-satisfied
/// seeds silently; a re-arm bumps the incident cycle; the alert is sent BEFORE the fired state is committed; and the
/// whole thing is R-20-scoped per owner while the indicator read is global.
/// </summary>
public class TriggerEvaluationServiceTests
{
    private const string Symbol = "ES";
    private const string Indicator = "rsi";
    private const int Period = 14;
    private const int Resolution = 1;
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly IIndicatorSource _indicators = A.Fake<IIndicatorSource>();
    private readonly INotificationChannel _notifications = A.Fake<INotificationChannel>();
    private readonly ITriggerReviewer _reviewer = A.Fake<ITriggerReviewer>();
    private static DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch.AddYears(56);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public TriggerEvaluationServiceTests()
    {
        // Accepted-for-delivery by default; the send-before-commit test overrides this to throw.
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);
    }

    private DbContextOptions<TradingCopilotDbContext> Options =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context(Guid? asUser = null) => new(Options, new FixedUser(asUser ?? _operator));

    private TriggerEvaluationService Service() => new(
        Context(), Options, _indicators, _notifications, _reviewer, NullLogger<TriggerEvaluationService>.Instance);

    private void ReviewerReturns(ReviewOutcome outcome) =>
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._)).Returns(outcome);

    private async Task<Guid> SeedAccountAsync(Guid? owner = null, TradingMode mode = TradingMode.Practice)
    {
        Guid ownerId = owner ?? _operator;
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Accounts.Add(new Account
        {
            Id = id,
            UserId = ownerId,
            ConnectionId = Guid.NewGuid(),
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Mode = mode,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private void IndicatorReturns(decimal? value) =>
        A.CallTo(() => _indicators.GetValueAsync(
                A<InstrumentId>._, A<string>._, A<int>._, A<int>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(value);

    private async Task<Guid> AddTriggerAsync(
        Guid? owner = null,
        IndicatorComparison comparison = IndicatorComparison.Below,
        decimal threshold = 30m,
        TriggerArmState armState = TriggerArmState.Armed,
        int armCycle = 0,
        TriggerRoute route = TriggerRoute.Mechanical,
        NotificationSeverity severity = NotificationSeverity.Notify,
        bool enabled = true,
        decimal? hysteresis = null,
        Guid? accountId = null,
        int? size = null)
    {
        Guid ownerId = owner ?? _operator;
        Guid id = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Triggers.Add(new TriggerRecord
        {
            Id = id,
            UserId = ownerId,
            Symbol = Symbol,
            Indicator = Indicator,
            Period = Period,
            ResolutionMinutes = Resolution,
            ConditionKind = TriggerConditionKind.IndicatorThreshold,
            Comparison = comparison,
            Threshold = threshold,
            Hysteresis = hysteresis,
            Route = route,
            AccountId = accountId,
            Size = size,
            Severity = severity,
            Enabled = enabled,
            ArmState = armState,
            ArmCycle = armCycle,
            CreatedAt = Now,
        });
        await context.SaveChangesAsync();
        return id;
    }

    // --- A crossing fires once, journals, and moves to Fired ---

    [Fact]
    public async Task ScanAsync_ShouldFireOnce_WhenAnArmedConditionBecomesSatisfied()
    {
        Guid id = await AddTriggerAsync(armState: TriggerArmState.Armed); // Below 30
        IndicatorReturns(25m);                                           // satisfied

        int fires = await Service().ScanAsync(Now, CancellationToken.None);

        fires.Should().Be(1);
        string expectedKey = $"trigger:{id}:0";
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == expectedKey), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        await using TradingCopilotDbContext reload = Context();
        TriggerRecord trigger = await reload.Triggers.SingleAsync(t => t.Id == id);
        trigger.ArmState.Should().Be(TriggerArmState.Fired);
        trigger.LastEvaluatedValue.Should().Be(25m);
        trigger.LastFiredAt.Should().Be(Now);

        TriggerFiringRecord firing = await reload.TriggerFirings.SingleAsync(f => f.TriggerId == id);
        firing.UserId.Should().Be(_operator);
        firing.ObservedValue.Should().Be(25m);
        firing.Threshold.Should().Be(30m);
        firing.Comparison.Should().Be(IndicatorComparison.Below);
        firing.DedupKey.Should().Be($"trigger:{id}:0");
    }

    [Fact]
    public async Task ScanAsync_ShouldUseTheTriggersSeverity_OnTheAlert()
    {
        await AddTriggerAsync(armState: TriggerArmState.Armed, severity: NotificationSeverity.Notify);
        IndicatorReturns(25m);

        await Service().ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.Severity == NotificationSeverity.Notify), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ScanAsync_ShouldFireOnceThenDebounce_WhenTheConditionStaysSatisfiedAcrossScans()
    {
        await AddTriggerAsync(armState: TriggerArmState.Armed);
        IndicatorReturns(25m);

        await Service().ScanAsync(Now, CancellationToken.None);
        await Service().ScanAsync(Now, CancellationToken.None);
        await Service().ScanAsync(Now, CancellationToken.None);

        // A continuously-satisfied level fires exactly once -- the arming edge -- and debounces thereafter.
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    // --- Fail-closed null ---

    [Fact]
    public async Task ScanAsync_ShouldNeverFire_AndHoldState_WhenTheIndicatorIsNull()
    {
        Guid id = await AddTriggerAsync(armState: TriggerArmState.Unseeded);
        // No IndicatorReturns configured -> the read yields null (cannot measure).

        int fires = await Service().ScanAsync(Now, CancellationToken.None);

        fires.Should().Be(0);
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Unseeded);
        (await reload.TriggerFirings.AnyAsync()).Should().BeFalse();
    }

    // --- Seed silently ---

    [Fact]
    public async Task ScanAsync_ShouldSeedToFiredSilently_WhenCreatedWhileAlreadySatisfied()
    {
        Guid id = await AddTriggerAsync(armState: TriggerArmState.Unseeded);
        IndicatorReturns(25m); // already satisfied at creation

        int fires = await Service().ScanAsync(Now, CancellationToken.None);

        // Adopts the pre-existing truth without firing: the first alert only ever comes from an observed edge.
        fires.Should().Be(0);
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
        (await reload.TriggerFirings.AnyAsync()).Should().BeFalse();
    }

    // --- Re-arm bumps the incident cycle ---

    [Fact]
    public async Task ScanAsync_ShouldResolveAndBumpTheCycle_OnReArm_SoTheNextCrossingIsANewIncident()
    {
        Guid id = await AddTriggerAsync(armState: TriggerArmState.Fired, armCycle: 0);

        // First scan: NotSatisfied (40 is above the below-30 line) -> re-arm the current incident, bump the cycle.
        IndicatorReturns(40m);
        await Service().ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _notifications.ResolveAsync($"trigger:{id}:0", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        await using (TradingCopilotDbContext mid = Context())
        {
            TriggerRecord midTrigger = await mid.Triggers.SingleAsync(t => t.Id == id);
            midTrigger.ArmCycle.Should().Be(1);
            midTrigger.ArmState.Should().Be(TriggerArmState.Armed);
        }

        // Second scan: satisfied again -> a fresh crossing that fires under the NEW cycle key.
        IndicatorReturns(25m);
        await Service().ScanAsync(Now, CancellationToken.None);

        string newCycleKey = $"trigger:{id}:1";
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == newCycleKey), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    // --- Disabled triggers are skipped entirely ---

    [Fact]
    public async Task ScanAsync_ShouldSkipDisabledTriggers_NeitherReadingNorFiring()
    {
        await AddTriggerAsync(enabled: false, armState: TriggerArmState.Armed);
        IndicatorReturns(25m); // would satisfy if it were read

        int fires = await Service().ScanAsync(Now, CancellationToken.None);

        fires.Should().Be(0);
        A.CallTo(() => _indicators.GetValueAsync(
                A<InstrumentId>._, A<string>._, A<int>._, A<int>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .MustNotHaveHappened(); // never even read -- not just never fired
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // --- Send-before-commit: neither a thrown send nor a rejected (false) send leaves a fired-but-unsent trigger ---

    [Fact]
    public async Task ScanAsync_ShouldLeaveTheTriggerUnfired_WhenTheSendThrows()
    {
        // The channel contract says SendAsync never throws, but if a buggy channel does, the per-owner guard catches
        // it and the fired state -- set in memory but not yet committed -- is discarded with the scoped context, so
        // the trigger stays Armed and re-attempts next pass rather than being left silently fired.
        Guid id = await AddTriggerAsync(armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("transport down"));

        int fires = await Service().ScanAsync(Now, CancellationToken.None); // caught per owner -- does not propagate

        fires.Should().Be(0);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Armed);
        (await reload.TriggerFirings.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ScanAsync_ShouldStayArmedAndNotJournal_WhenTheSendIsNotAccepted()
    {
        // The real production channel returns false (never throws) when it cannot accept the alert -- e.g. a wedged
        // transport has filled the bounded queue. A non-delivery must NOT commit Fired: that would be a lost alert
        // with a firing row that lies. The trigger stays Armed so the next pass re-attempts the send.
        Guid id = await AddTriggerAsync(armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(false);

        int fires = await Service().ScanAsync(Now, CancellationToken.None);

        fires.Should().Be(0);
        await using (TradingCopilotDbContext reload = Context())
        {
            TriggerRecord trigger = await reload.Triggers.SingleAsync(t => t.Id == id);
            trigger.ArmState.Should().Be(TriggerArmState.Armed);
            trigger.LastFiredAt.Should().BeNull();
            (await reload.TriggerFirings.AnyAsync()).Should().BeFalse();
        }

        // The next pass re-attempts the send rather than debouncing the never-delivered alert away.
        await Service().ScanAsync(Now, CancellationToken.None);
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    // --- R-20: two owners, each in their own context, owner-stamped firings ---

    [Fact]
    public async Task ScanAsync_ShouldFireForEachOwnerInTheirOwnContext_WithOwnerStampedFirings()
    {
        Guid ownerA = _operator;
        Guid ownerB = Guid.NewGuid();
        Guid idA = await AddTriggerAsync(owner: ownerA, armState: TriggerArmState.Armed);
        Guid idB = await AddTriggerAsync(owner: ownerB, armState: TriggerArmState.Armed);
        IndicatorReturns(25m); // the global indicator read serves both owners' same series

        int fires = await Service().ScanAsync(Now, CancellationToken.None);

        fires.Should().Be(2);

        // Each firing is scoped and stamped to its owner -- owner A's SaveChanges never persisted owner B's row.
        await using TradingCopilotDbContext reloadA = Context(ownerA);
        TriggerFiringRecord firingA = await reloadA.TriggerFirings.SingleAsync();
        firingA.UserId.Should().Be(ownerA);
        firingA.TriggerId.Should().Be(idA);

        await using TradingCopilotDbContext reloadB = Context(ownerB);
        TriggerFiringRecord firingB = await reloadB.TriggerFirings.SingleAsync();
        firingB.UserId.Should().Be(ownerB);
        firingB.TriggerId.Should().Be(idB);
    }

    // =====================================================================================================
    // AGENT-REVIEW route (gh#402, ADR-0008): a fire wakes the reviewer once per arming edge, stages a Suggestion
    // (never an order) on a coherent proposal, and journals the firing regardless -- COMMIT-THEN-NOTIFY.
    // =====================================================================================================

    // (a) A coherent suggestion is staged once, sized from the trigger, moded live from the account, then advised.
    [Fact]
    public async Task ScanAsync_ShouldStageOneSuggestionAndAdvise_WhenAgentReviewFiresWithACoherentSuggestion()
    {
        Guid accountId = await SeedAccountAsync(mode: TradingMode.Practice);
        Guid id = await AddTriggerAsync(
            route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold"));

        await Service().ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        Suggestion suggestion = await reload.Suggestions.SingleAsync();
        suggestion.UserId.Should().Be(_operator);
        suggestion.AccountId.Should().Be(accountId);
        suggestion.Instrument.Should().Be(Symbol);
        suggestion.Side.Should().Be(OrderSide.Buy);
        suggestion.Size.Should().Be(3);                     // from the TRIGGER, never the model
        suggestion.EntryPrice.Should().Be(100m);
        suggestion.StopPrice.Should().Be(99m);
        suggestion.TargetPrice.Should().Be(103m);
        suggestion.Mode.Should().Be(TradingMode.Practice);  // read LIVE from the account
        suggestion.State.Should().Be(SuggestionState.Active);

        TriggerRecord trigger = await reload.Triggers.SingleAsync(t => t.Id == id);
        trigger.ArmState.Should().Be(TriggerArmState.Fired);
        trigger.LastFiredAt.Should().Be(Now);
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();

        // The advisory is best-effort and sent AFTER the commit (commit-then-notify).
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    // (b) The review runs ONCE per arming edge -- a persistently-true condition must not re-review every pass.
    [Fact]
    public async Task ScanAsync_ShouldReviewOncePerArmCycle_NotEveryPass()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));

        await Service().ScanAsync(Now, CancellationToken.None);
        await Service().ScanAsync(Now, CancellationToken.None);
        await Service().ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        (await Context().Suggestions.CountAsync()).Should().Be(1); // no duplicate on the debounced passes
    }

    // (c) NotWorthSurfacing: no suggestion, no notify -- but the fire is still journaled and the arm advances.
    [Fact]
    public async Task ScanAsync_ShouldFireSilently_WhenAgentReviewSuppressesNotWorthSurfacing()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suppress(SuppressReason.NotWorthSurfacing, "chop"));

        await Service().ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.AnyAsync()).Should().BeFalse();
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // (d) NoReviewerConfigured: a fallback advisory tells the operator a setup fired that could not be reviewed.
    [Fact]
    public async Task ScanAsync_ShouldSendAFallbackAdvisory_WhenNoReviewerIsConfigured()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suppress(SuppressReason.NoReviewerConfigured, "no LLM reviewer is configured"));

        await Service().ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.AnyAsync()).Should().BeFalse();
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    // (e) An incoherent proposal (a Buy with the stop ABOVE entry) is rejected by SuggestionGeometry before persist.
    [Fact]
    public async Task ScanAsync_ShouldStageNoSuggestion_WhenTheProposedGeometryIsIncoherent()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 105m, 110m, "broken"));

        await Service().ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.AnyAsync()).Should().BeFalse();
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // (f) An undeclared account mode cannot be traded -- nothing is suggested on it (mode is read live).
    [Fact]
    public async Task ScanAsync_ShouldStageNoSuggestion_WhenTheAccountModeIsUndeclared()
    {
        Guid accountId = await SeedAccountAsync(mode: TradingMode.Undeclared);
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));

        await Service().ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.AnyAsync()).Should().BeFalse();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
    }

    // (g) COMMIT-THEN-NOTIFY: a failed advisory leaves the Suggestion durable and never re-arms (no duplicate).
    [Fact]
    public async Task ScanAsync_ShouldKeepTheSuggestionAndStayFired_WhenTheAdvisoryNotifyFails()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 2, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(false);

        await Service().ScanAsync(Now, CancellationToken.None);

        (await Context().Suggestions.CountAsync()).Should().Be(1); // durable regardless of the best-effort notify
        TriggerRecord trigger = await Context().Triggers.SingleAsync(t => t.Id == id);
        trigger.ArmState.Should().Be(TriggerArmState.Fired);
        trigger.LastFiredAt.Should().Be(Now);

        // A second pass must NOT re-review or duplicate -- re-arming on a failed notify would do exactly that.
        await Service().ScanAsync(Now, CancellationToken.None);
        (await Context().Suggestions.CountAsync()).Should().Be(1);
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    // (h) A mechanical trigger in the same scan still fires its alert unchanged, alongside the agent-review route.
    [Fact]
    public async Task ScanAsync_ShouldStillFireMechanicalUnchanged_AlongsideAgentReview()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.Mechanical, armState: TriggerArmState.Armed);
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suppress(SuppressReason.NotWorthSurfacing, "x")); // agent-review stays silent

        int fires = await Service().ScanAsync(Now, CancellationToken.None);

        fires.Should().Be(2); // the mechanical alert and the agent-review fire
        await using TradingCopilotDbContext reload = Context();
        (await reload.TriggerFirings.CountAsync()).Should().Be(2);

        // Exactly one send: the mechanical alert (the agent-review suppressed silently).
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }
}
