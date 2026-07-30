using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
    private readonly IReviewEnrichmentSource _enrichment = A.Fake<IReviewEnrichmentSource>();
    private readonly IAiUsageLedger _ledger = A.Fake<IAiUsageLedger>();
    private readonly ILlmMetrics _llmMetrics = A.Fake<ILlmMetrics>();
    private static DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch.AddYears(56);

    // The cost a real LLM call surfaces (gh#431). Any non-null Cost makes the scan record a usage row; the SPECIFIC
    // values here don't matter to the pre-existing tests -- only that Cost is non-null so the recording path is exercised.
    private static readonly AiCallCost _sampleCost = new(
        AiUsageFeature.Triage, "claude-haiku-4-5", LlmModelTier.Triage, AiUsageOutcome.Succeeded,
        42, 9, 0.0006m, TimeSpan.FromMilliseconds(1234));

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public TriggerEvaluationServiceTests()
    {
        // Accepted-for-delivery by default; the send-before-commit test overrides this to throw.
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);
    }

    private DbContextOptions<TradingCopilotDbContext> Options =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context(Guid? asUser = null) => new(Options, new FixedUser(asUser ?? _operator));

    // Every existing test defaults to an INERT governor (a bare GovernorOptions has DailyBudgetUsd null => no cap),
    // so behaviour is byte-for-byte unchanged from before gh#448. The gh#448 cases pass a configured GovernorOptions.
    private TriggerEvaluationService Service(
        IAiUsageLedger? ledger = null, GovernorOptions? governor = null, IReviewEnrichmentSource? enrichment = null) => new(
        Context(), Options, _indicators, _notifications, _reviewer, enrichment ?? _enrichment, ledger ?? _ledger,
        _llmMetrics, new AiSpendGovernor(),
        Microsoft.Extensions.Options.Options.Create(governor ?? new GovernorOptions()),
        NullLogger<TriggerEvaluationService>.Instance);

    // The reviewer returns an AgentReview: the outcome the route acts on plus the LLM-call cost(s) the scan ledgers,
    // one row per billed call (gh#449). Passing no costs defaults to a single _sampleCost, so every EXISTING caller
    // (which passes only an outcome) is behaviour-unchanged -- still exactly one billed triage call. An escalated fire
    // is modelled by passing TWO costs (triage + deep).
    private void ReviewerReturns(ReviewOutcome outcome, params AiCallCost[] costs) =>
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._))
            .Returns(new AgentReview(outcome, costs.Length == 0 ? [_sampleCost] : costs));

    private void ReviewerThrows(Exception error) =>
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).Throws(error);

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
        int? size = null,
        TriggerConfirmation confirmation = TriggerConfirmation.Confirmed)
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
            Confirmation = confirmation,
            ArmState = armState,
            ArmCycle = armCycle,
            CreatedAt = Now,
        });
        await context.SaveChangesAsync();
        return id;
    }

    // Seeds one AIUsage spend row (gh#431 shape) under the given owner. The governor's per-pass read is PLATFORM-WIDE
    // (IgnoreQueryFilters), and the in-memory DB is shared across contexts by database name, so a row seeded under ANY
    // owner -- an operator, a stranger, or the SystemOwner embed sentinel -- is summed into the gate's floor.
    private async Task SeedUsageAsync(Guid owner, decimal costUsd, DateTimeOffset occurredAt)
    {
        await using TradingCopilotDbContext context = Context(owner);
        context.AiUsage.Add(new AiUsageRecord
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            Feature = AiUsageFeature.Triage,
            Model = "claude-haiku-4-5",
            Tier = LlmModelTier.Triage,
            Outcome = AiUsageOutcome.Succeeded,
            InputTokens = 0,
            OutputTokens = 0,
            EstimatedCostUsd = costUsd,
            LatencyMs = 0,
            OccurredAt = occurredAt,
        });
        await context.SaveChangesAsync();
    }

    // --- The confirmation gate: an unconfirmed trigger is inert regardless of Enabled (gh#470) ---

    [Fact]
    public async Task ScanAsync_ShouldNotFire_WhenTheTriggerIsUnconfirmed_EvenEnabledAndSatisfied()
    {
        // The whole point of the gate: an authored-but-unconfirmed trigger never fires, whatever Enabled says. This
        // one is Enabled, Armed, and its condition is satisfied -- the exact setup that fires a confirmed trigger --
        // but it has never been accepted into the firing set, so the scan must neither discover nor evaluate it.
        Guid id = await AddTriggerAsync(armState: TriggerArmState.Armed, confirmation: TriggerConfirmation.Unconfirmed);
        IndicatorReturns(25m); // Below 30 -- satisfied

        int fires = await Service().ScanAsync(Now, CancellationToken.None);

        fires.Should().Be(0, "an unconfirmed trigger is inert regardless of Enabled (gh#470)");
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();

        await using TradingCopilotDbContext reload = Context();
        TriggerRecord trigger = await reload.Triggers.SingleAsync(t => t.Id == id);
        trigger.ArmState.Should().Be(TriggerArmState.Armed, "an unevaluated trigger's debounce is untouched");
        trigger.LastEvaluatedValue.Should().BeNull("the scan never read it");
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeFalse();
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

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
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
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
    }

    // (i) A THROWING reviewer must still debounce: the seam admits a provider-backed reviewer that throws on a
    // transient fault, and a throw must be treated fail-closed -- no suggestion, the arm still advances to Fired (so
    // it is NOT re-reviewed every pass), the firing is journaled, and the operator is advised. A throw must never
    // escape to abort the owner pass.
    [Fact]
    public async Task ScanAsync_ShouldDebounceAndAdvise_WhenTheReviewerThrows()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerThrows(new InvalidOperationException("provider 503"));

        int fires = await Service().ScanAsync(Now, CancellationToken.None); // must NOT propagate

        fires.Should().Be(1);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.AnyAsync()).Should().BeFalse();                          // fail-closed: no suggestion
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();  // a fire is a fire
        TriggerRecord trigger = await reload.Triggers.SingleAsync(t => t.Id == id);
        trigger.ArmState.Should().Be(TriggerArmState.Fired);                               // debounced -- not left Armed
        trigger.LastFiredAt.Should().Be(Now);
        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        // The debounce holds across passes: a persistently-throwing reviewer is asked exactly once per arming edge.
        await Service().ScanAsync(Now, CancellationToken.None);
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
    }

    // (j) A reviewer throw must NOT starve a co-owner's mechanical alert -- the throw is contained, not propagated.
    [Fact]
    public async Task ScanAsync_ShouldStillFireMechanical_WhenACoOwnerAgentReviewReviewerThrows()
    {
        Guid accountId = await SeedAccountAsync();
        Guid mechanicalId = await AddTriggerAsync(route: TriggerRoute.Mechanical, armState: TriggerArmState.Armed);
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerThrows(new InvalidOperationException("provider down"));

        int fires = await Service().ScanAsync(Now, CancellationToken.None);

        fires.Should().Be(2); // the mechanical fire + the agent-review fire (fail-closed), neither lost to the throw
        await using TradingCopilotDbContext reload = Context();
        (await reload.TriggerFirings.CountAsync()).Should().Be(2);
        (await reload.Triggers.SingleAsync(t => t.Id == mechanicalId)).ArmState.Should().Be(TriggerArmState.Fired);
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

    // =====================================================================================================
    // AIUsage LEDGER (gh#431, ADR-0008 / ADR-0002): the scan is the SINGLE tenancy authority for the spend row. On an
    // agent-review fire with a real (billed) reviewer call, it records ONE usage entry stamped with the FIRING owner;
    // with a no-call (null-cost) review it records NOTHING; and a ledger fault must never roll back the fire (fail-open).
    // =====================================================================================================

    // The load-bearing tenancy assertion: the recorded owner is the owner of the trigger that fired, NOT the discovery
    // context's user -- so a co-tenant is never billed for another operator's call (R-20).
    [Fact]
    public async Task ScanAsync_RecordsUsageStampedWithTheFiringOwner_OnAnAgentReviewFire()
    {
        // A firing owner DISTINCT from the discovery user (_operator, via Context()) makes "owner == firing owner" the
        // load-bearing check: the ledger entry must be stamped from the per-owner scope, never the discovery context.
        Guid firingOwner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner: firingOwner);
        await AddTriggerAsync(
            owner: firingOwner, route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold"));

        AiUsageEntry? captured = null;
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._))
            .Invokes((AiUsageEntry entry, CancellationToken _) => captured = entry);

        await Service().ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        captured.Should().NotBeNull();
        captured!.UserId.Should().Be(firingOwner); // <-- THE tenancy assertion: recorded owner == the fired trigger's owner
        captured.Cost.Feature.Should().Be(AiUsageFeature.Triage);
        captured.OccurredAt.Should().Be(Now);       // the caller-supplied clock, threaded through -- the ledger reads none
    }

    // A no-call (inert-reviewer) review carries EMPTY Costs, and the scan must then record NOTHING. The scan records
    // one row per cost (foreach over Costs), so an empty Costs writes zero rows -- there is no spend to record for a
    // call never made (gh#449).
    [Fact]
    public async Task ScanAsync_RecordsNoUsage_WhenTheReviewerReturnsNoCosts()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._))
            .Returns(new AgentReview(
                new ReviewOutcome.Suppress(SuppressReason.NoReviewerConfigured, "no LLM reviewer is configured"), Costs: []));

        await Service().ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // ONE ROW PER COST (gh#449): an escalated review made TWO billed calls (triage + deep), so the scan writes TWO
    // AIUsage rows -- one per AiCallCost, in call order -- both stamped with the FIRING owner. A real AiUsageLedger
    // (not the fake) actually persists, so the count is the load-bearing proof both rows landed.
    [Fact]
    public async Task ScanAsync_ShouldRecordOneRowPerCost_WhenTheReviewMadeTwoCalls()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);

        AiCallCost triageCost = new(
            AiUsageFeature.Triage, "claude-haiku-4-5", LlmModelTier.Triage, AiUsageOutcome.Succeeded,
            40, 8, 0.0005m, TimeSpan.FromMilliseconds(120));
        AiCallCost deepCost = new(
            AiUsageFeature.Triage, "claude-sonnet-5", LlmModelTier.Deep, AiUsageOutcome.Succeeded,
            900, 300, 0.0072m, TimeSpan.FromMilliseconds(800));
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold"), triageCost, deepCost);

        // A REAL ledger so both spend rows are actually persisted (the fake records nothing to count).
        IAiUsageLedger realLedger = new AiUsageLedger(Options, NullLogger<AiUsageLedger>.Instance);
        await Service(ledger: realLedger).ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        List<AiUsageRecord> rows = await reload.AiUsage.ToListAsync();
        rows.Should().HaveCount(2);                                   // one row per billed call
        rows.Should().OnlyContain(row => row.UserId == _operator);    // both stamped with the firing owner (R-20)
    }

    // FAIL-OPEN AT THE LEDGER BOUNDARY: the scan calls _ledger.RecordAsync UNGUARDED -- the fail-open lives inside the
    // real AiUsageLedger, which swallows its own write fault. So a DB blip while recording spend must never roll back
    // the fired setup's suggestion or its firing. This composes the PRODUCTION shape (the real fail-open ledger whose
    // underlying write throws), NOT a contract-violating throwing fake, and asserts the fire still commits.
    [Fact]
    public async Task ScanAsync_StillFiresAndCommits_WhenTheLedgerWriteFaults()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 2, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold"));

        IAiUsageLedger failingLedger = new ThrowingWriteLedger(Options);
        int fires = await Service(failingLedger).ScanAsync(Now, CancellationToken.None); // must NOT throw

        fires.Should().Be(1);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.CountAsync()).Should().Be(1);                         // durable despite the ledger fault
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
        (await reload.AiUsage.AnyAsync()).Should().BeFalse(); // the spend row was lost -- the ledger is a floor, not a guarantee
    }

    // DEFENSE IN DEPTH at the scan boundary: even a ledger that VIOLATES its never-throw contract (throws straight out
    // of RecordAsync, not from an inner write it swallows) must not roll back the fire. The scan guards the ledger call
    // exactly as it guards the reviewer and advisory-notify seams -- an UN-guarded throw would unwind to the per-owner
    // catch and discard the whole pass (losing the suggestion + firing here, AND a co-owner's already-sent mechanical
    // alert -- a duplicate next pass). With the guard, the fire commits; only the spend row is lost.
    [Fact]
    public async Task ScanAsync_StillFiresAndCommits_WhenAContractViolatingLedgerThrowsFromRecord()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 2, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold"));
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("a ledger that breaks its never-throw contract"));

        int fires = await Service().ScanAsync(Now, CancellationToken.None); // the boundary guard swallows it -- pass NOT discarded

        fires.Should().Be(1);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.CountAsync()).Should().Be(1);                          // committed, not rolled back
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
    }

    // The scan-boundary ledger guard must NOT mask the caller's OWN cancellation -- a shutdown drain has to propagate,
    // never be swallowed as a bookkeeping fault. A cancelled-token OCE from the ledger surfaces out of ScanAsync (via
    // the per-owner cancellation guard), distinct from the swallow-and-continue of any other ledger fault above.
    [Fact]
    public async Task ScanAsync_Propagates_WhenTheLedgerObservesCallerCancellation()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));
        using CancellationTokenSource cts = new();
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._))
            .Invokes(() => cts.Cancel())
            .Throws(new OperationCanceledException(cts.Token));

        Func<Task> scan = () => Service().ScanAsync(Now, cts.Token);

        await scan.Should().ThrowAsync<OperationCanceledException>();
    }

    // =====================================================================================================
    // AI-SPEND GOVERNOR (gh#448, ADR-0008): a PURE budget gate the scan consults once per pass and before each
    // agent-review LLM call. It caps WHETHER a call is made (cost), never what it proposes. It gates the AGENT-REVIEW
    // route ONLY -- the mechanical route is untouched -- and is FAIL-OPEN (the deliberate inverse of the risk gate).
    // Inert until a budget is configured, so every test above (default GovernorOptions) is behaviour-unchanged.
    // =====================================================================================================

    // (a) BUDGET EXHAUSTED: with spend over the cap, the reviewer is NOT called and NO spend row is written; the fire
    // is still journaled + debounced and the operator gets one "review is paused" advisory per arming edge.
    [Fact]
    public async Task ScanAsync_ShouldBlockTheReviewAndAdvise_WhenTheBudgetIsExhausted()
    {
        await SeedUsageAsync(_operator, 100m, Now); // 100 spent against a 10 budget -> the governor blocks
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be")); // must never be reached

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustNotHaveHappened();
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustNotHaveHappened();

        await using TradingCopilotDbContext reload = Context();
        (await reload.AiUsage.IgnoreQueryFilters().CountAsync()).Should().Be(1); // only the seeded row -- no call, no new spend
        (await reload.Suggestions.AnyAsync()).Should().BeFalse();
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();       // a fire is still a fire
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.Severity == NotificationSeverity.Notify && n.Body.Contains("agent review is paused")),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    // (b) UNDER BUDGET: with room under the cap, the agent-review route runs exactly the normal suggest path.
    [Fact]
    public async Task ScanAsync_ShouldReviewNormally_WhenSpendIsUnderBudget()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold"));

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.CountAsync()).Should().Be(1);
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
    }

    // (c) INERT (unconfigured): a null budget leaves the governor off -- no spend read, no gating, and NO threshold alert.
    [Fact]
    public async Task ScanAsync_ShouldRunUnGatedWithNoThresholdAlert_WhenNoBudgetIsConfigured()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));

        await Service().ScanAsync(Now, CancellationToken.None); // default GovernorOptions => DailyBudgetUsd null => inert

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey.StartsWith("ai-spend:threshold:")), A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // =====================================================================================================
    // BUDGET-AWARE ESCALATION SKIP (gh#478): the pass-level governor caps WHETHER the review runs; this caps whether the
    // cheap triage may escalate to the expensive DEEP tier. The scan holds the tally + the budget, so it decides
    // affordability (spent + the reviewer's conservative deep-call estimate <= budget) and passes the reviewer a plain
    // permission bit -- never the budget (the gh#449 purity constraint). These assert the scan computes + threads the bit.
    // =====================================================================================================

    // AFFORDABLE: spend leaves room for a full triage->deep pair, so escalation is permitted (allowEscalate == true).
    [Fact]
    public async Task ScanAsync_ShouldPermitEscalation_WhenADeepCallStillFitsTheBudget()
    {
        await SeedUsageAsync(_operator, 5m, Now); // 5 spent of a 10 budget -> not blocked, and 5 + 2 <= 10
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        A.CallTo(() => _reviewer.EstimatedDeepCallCostUsd).Returns(2m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, true))
            .MustHaveHappenedOnceExactly();
    }

    // UNAFFORDABLE: the cheap triage still fits (so the review runs), but a triage->deep PAIR would overrun -- the
    // partial-budget case. Escalation is refused (allowEscalate == false), holding the pair-overrun ADR-0008 named.
    [Fact]
    public async Task ScanAsync_ShouldRefuseEscalation_WhenADeepCallWouldOverrunTheBudget()
    {
        await SeedUsageAsync(_operator, 9m, Now); // 9 spent of a 10 budget -> NOT blocked (triage fits), but 9 + 2 > 10
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        A.CallTo(() => _reviewer.EstimatedDeepCallCostUsd).Returns(2m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        // The reviewer WAS called (triage is affordable) -- but with escalation refused.
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, false))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, true))
            .MustNotHaveHappened();
    }

    // INERT GOVERNOR: no budget configured -> un-gated, so escalation is permitted exactly as before gh#478.
    [Fact]
    public async Task ScanAsync_ShouldPermitEscalation_WhenNoBudgetIsConfigured()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        A.CallTo(() => _reviewer.EstimatedDeepCallCostUsd).Returns(999m); // irrelevant with no budget -> still allowed
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));

        await Service().ScanAsync(Now, CancellationToken.None); // default GovernorOptions => inert

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, true))
            .MustHaveHappenedOnceExactly();
    }

    // The operator is TOLD when a hard setup couldn't get its deeper look: EscalationDeclined -> a budget-framed advisory.
    [Fact]
    public async Task ScanAsync_ShouldAdviseTheOperator_WhenTheReviewerReportsEscalationDeclined()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suppress(SuppressReason.EscalationDeclined, "escalation not permitted"));

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        // Fail-closed but not silent: a Notify advisory naming the budget, and the fire still journals + debounces.
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n =>
                    n.Severity == NotificationSeverity.Notify && n.Body.Contains("deeper look")),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.AnyAsync()).Should().BeFalse();
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
    }

    // (d) EMPTY WINDOW -> 0 -> allow: with no usage rows, the read yields 0 and the pass fires normally. NOTE: this
    // runs on the EF in-memory provider, where SUM over an empty set returns 0 REGARDLESS of the `(decimal?)` cast,
    // so it does NOT witness the nullable-projection guard. On real Postgres an empty-window SUM returns NULL and the
    // non-nullable overload would throw (then the fail-open catch would silently un-gate) -- that relational NULL
    // path is QA integration-tier (flagged on gh#448). This unit only proves an empty ledger reads as 0 -> allow.
    [Fact]
    public async Task ScanAsync_ShouldReadAnEmptyLedgerAsZeroAndAllow_WhenNoUsageRowsExist()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));

        int fires = await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        fires.Should().Be(1);
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
    }

    // (e) FAIL-OPEN on read fault: a faulting spend read must let the pass run UN-GATED (the reviewer still wakes) and
    // never abort the pass -- the deliberate INVERSE of the fail-closed risk gate.
    [Fact]
    public async Task ScanAsync_ShouldFailOpenAndStillReview_WhenTheSpendReadFaults()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));

        TriggerEvaluationService service = new ThrowingReadService(
            Context(), Options, _indicators, _notifications, _reviewer, _enrichment, _ledger, _llmMetrics, new AiSpendGovernor(),
            Microsoft.Extensions.Options.Options.Create(new GovernorOptions { DailyBudgetUsd = 10m }),
            NullLogger<TriggerEvaluationService>.Instance);

        Func<Task> act = () => service.ScanAsync(Now, CancellationToken.None);

        // INVERSE of the fail-closed risk gate: a spend-read blip must NOT pause agent review and must NOT abort the
        // pass (which would also kill the co-located mechanical route). DO NOT "fix" this to fail-closed by analogy.
        await act.Should().NotThrowAsync();
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
    }

    // (f) PLATFORM-WIDE SUM crosses R-20: three owners each spend 4 (sum 12 > budget 10) -- the operator, an unrelated
    // stranger, and the SystemOwner embed sentinel. Only the operator has a trigger, but the IgnoreQueryFilters read
    // must fold in ALL owners + the sentinel, so the operator's fire is blocked by spend that is not theirs alone.
    [Fact]
    public async Task ScanAsync_ShouldSumSpendAcrossAllOwnersAndTheSystemSentinel_WhenGating()
    {
        Guid stranger = Guid.NewGuid();
        await SeedUsageAsync(_operator, 4m, Now);
        await SeedUsageAsync(stranger, 4m, Now);
        await SeedUsageAsync(SystemOwner.Id, 4m, Now);
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be")); // must never be reached

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustNotHaveHappened();
        (await Context().Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
    }

    // (g) WITHIN-PASS TALLY: budget == exactly one fire's cost, no seeded usage, TWO agent-review fires this pass.
    // fire1 Evaluate(cost, 0) => Allow (accrues cost -> spent = cost); fire2 Evaluate(cost, cost) => Block. So the
    // reviewer is asked ONCE and exactly ONE spend row lands -- later fires this pass see RISING spend (the per-fire
    // mirror of the risk gate consuming the day's loss).
    [Fact]
    public async Task ScanAsync_ShouldBlockTheSecondFireThisPass_WhenTheFirstFireExhaustsTheBudgetViaTheTally()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x")); // Cost defaults to _sampleCost

        // A REAL ledger so an authorized fire actually writes its spend row (the fake records nothing to count).
        IAiUsageLedger realLedger = new AiUsageLedger(Options, NullLogger<AiUsageLedger>.Instance);
        await Service(ledger: realLedger, governor: new GovernorOptions { DailyBudgetUsd = _sampleCost.EstimatedCostUsd })
            .ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        (await Context().AiUsage.CountAsync()).Should().Be(1); // only the first (allowed) fire wrote a spend row
    }

    // (g2) ESCALATION ACCRUES BOTH COSTS (gh#449): the within-pass tally must fold in EVERY billed call, not just the
    // first. Budget == the SUM of the escalated fire's two costs; two agent-review triggers fire this pass. fire1
    // accrues BOTH the triage and deep cost -> spent == budget, so fire2 sees spent == budget and is blocked. Had only
    // one of the two costs accrued, spent would stay under budget and fire2 would wrongly proceed.
    [Fact]
    public async Task ScanAsync_ShouldAccrueEveryCost_WhenTheReviewEscalated()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);

        AiCallCost triageCost = new(
            AiUsageFeature.Triage, "claude-haiku-4-5", LlmModelTier.Triage, AiUsageOutcome.Succeeded,
            40, 8, 0.0005m, TimeSpan.FromMilliseconds(120));
        AiCallCost deepCost = new(
            AiUsageFeature.Triage, "claude-sonnet-5", LlmModelTier.Deep, AiUsageOutcome.Succeeded,
            900, 300, 0.0072m, TimeSpan.FromMilliseconds(800));
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"), triageCost, deepCost);

        // Budget is EXACTLY the two costs' sum, so accruing both (and only both) exhausts it after the first fire.
        decimal budget = triageCost.EstimatedCostUsd + deepCost.EstimatedCostUsd;
        IAiUsageLedger realLedger = new AiUsageLedger(Options, NullLogger<AiUsageLedger>.Instance);
        await Service(ledger: realLedger, governor: new GovernorOptions { DailyBudgetUsd = budget })
            .ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        (await Context().AiUsage.CountAsync()).Should().Be(2); // both spend rows from the single authorized fire
    }

    // (h) MECHANICAL UNAFFECTED: the governor gates the LLM route ONLY. With the budget exhausted, the mechanical
    // alert must still send + journal exactly as before gh#448, while the co-located agent-review fire is blocked.
    [Fact]
    public async Task ScanAsync_ShouldStillFireMechanical_WhenTheBudgetBlocksTheAgentReviewRoute()
    {
        await SeedUsageAsync(_operator, 100m, Now);
        Guid mechanicalId = await AddTriggerAsync(route: TriggerRoute.Mechanical, armState: TriggerArmState.Armed);
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be")); // must never be reached

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustNotHaveHappened();
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == $"trigger:{mechanicalId}:0"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        (await Context().TriggerFirings.AnyAsync(f => f.TriggerId == mechanicalId)).Should().BeTrue();
    }

    // (i) THRESHOLD ALERT: spend seeded AT the 80% threshold (8 of 10). The fire is still under budget (8 < 10) so the
    // reviewer runs; the post-loop threshold pre-alert then fires exactly once, dedup-keyed by the Central trading date.
    [Fact]
    public async Task ScanAsync_ShouldSendOneThresholdAlert_WhenSpendReachesTheAlertFraction()
    {
        await SeedUsageAsync(_operator, 8m, Now);
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m, AlertThresholdFraction = 0.8m })
            .ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.Severity == NotificationSeverity.Notify && n.DedupKey.StartsWith("ai-spend:threshold:")),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    // (j) THRESHOLD ALERT WORDING when ALREADY over budget: the daily heads-up must say "reached", not "nearing"
    // (ThresholdReached is forced true on a Block, so gating on it alone would title an exhausted day "nearing").
    [Fact]
    public async Task ScanAsync_ShouldTitleTheThresholdAlertReached_WhenSpendIsOverBudget()
    {
        await SeedUsageAsync(_operator, 100m, Now); // 100 spent against a 10 budget -> over budget, the fire is blocked
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be"));

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n =>
                    n.DedupKey.StartsWith("ai-spend:threshold:") && n.Title == "AI daily budget reached"),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    // =====================================================================================================
    // DEEP-TIER ENRICHMENT (gh#476): the scan assembles the numeric market context AS OF the fire and attaches it to
    // the review context BEFORE waking the reviewer -- fail-open, and skipped entirely when the budget short-circuits.
    // =====================================================================================================

    // The scan builds enrichment for the fired trigger (as of the fire) and hands the reviewer the ENRICHED context.
    [Fact]
    public async Task ScanAsync_ShouldEnrichTheReviewContext_WhenAnAgentReviewFires()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);

        ReviewEnrichment enrichment = new(
            [new BarSnapshot(Now.AddMinutes(-1), 100m, 101m, 99m, 100.5m, 1234)],
            [new IndicatorValueSnapshot(Now.AddMinutes(-1), 28m)]);
        A.CallTo(() => _enrichment.BuildAsync(A<TriggerReviewContext>._, A<CancellationToken>._)).Returns(enrichment);

        TriggerReviewContext? captured = null;
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._))
            .Invokes((TriggerReviewContext c, CancellationToken _, bool _) => captured = c)
            .Returns(new AgentReview(new ReviewOutcome.Suppress(SuppressReason.NotWorthSurfacing, "x"), []));

        await Service().ScanAsync(Now, CancellationToken.None);

        // The enricher was asked for THIS trigger's context, as of the fire -- and its result rode onto the context the
        // reviewer then saw (the deep tier reads it; a triage-only reviewer simply ignores it).
        A.CallTo(() => _enrichment.BuildAsync(
                A<TriggerReviewContext>.That.Matches(c => c.TriggerId == id && c.FiredAt == Now), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        captured.Should().NotBeNull();
        captured!.Enrichment.Should().BeSameAs(enrichment);
    }

    // FAIL-OPEN: an enrichment read fault must NOT abort the fire -- the reviewer still runs, on an UN-enriched context.
    [Fact]
    public async Task ScanAsync_ShouldReviewUnEnriched_WhenTheEnrichmentSourceThrows()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        A.CallTo(() => _enrichment.BuildAsync(A<TriggerReviewContext>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("enrichment DB fault"));

        TriggerReviewContext? captured = null;
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._))
            .Invokes((TriggerReviewContext c, CancellationToken _, bool _) => captured = c)
            .Returns(new AgentReview(new ReviewOutcome.Suppress(SuppressReason.NotWorthSurfacing, "x"), []));

        Func<Task> act = () => Service().ScanAsync(Now, CancellationToken.None);

        await act.Should().NotThrowAsync();
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        captured.Should().NotBeNull();
        captured!.Enrichment.Should().BeNull();                                                    // un-enriched, not a lost fire
        (await Context().TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();        // a fire is still a fire
    }

    // The budget short-circuit is BEFORE enrichment: a blocked fire wakes neither the reviewer nor the enricher (no
    // wasted read to assemble context for a call that will never happen).
    [Fact]
    public async Task ScanAsync_ShouldNotEnrich_WhenTheBudgetBlocksTheReview()
    {
        await SeedUsageAsync(_operator, 100m, Now); // 100 spent against a 10 budget -> blocked before the reviewer
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be")); // must never be reached

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustNotHaveHappened();
        A.CallTo(() => _enrichment.BuildAsync(A<TriggerReviewContext>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // =====================================================================================================
    // LLM-SPEND METER (gh#477): every billed LLM call is metered — one RecordLlmCall per AiCallCost (two on an
    // escalation) — fed the same cost as the ledger, and metered even when the ledger write faults (independent sinks).
    // =====================================================================================================

    [Fact]
    public async Task ScanAsync_ShouldMeterTheLlmCall_WhenAnAgentReviewFires()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x")); // one billed triage call (_sampleCost)

        await Service().ScanAsync(Now, CancellationToken.None);

        // Metered exactly once, with the same cost the ledger got.
        A.CallTo(() => _llmMetrics.RecordLlmCall(_sampleCost)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ScanAsync_ShouldMeterBothCalls_WhenTheReviewEscalated()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);

        AiCallCost triageCost = new(
            AiUsageFeature.Triage, "claude-haiku-4-5", LlmModelTier.Triage, AiUsageOutcome.Succeeded,
            40, 8, 0.0005m, TimeSpan.FromMilliseconds(120));
        AiCallCost deepCost = new(
            AiUsageFeature.Triage, "claude-sonnet-5", LlmModelTier.Deep, AiUsageOutcome.Succeeded,
            900, 300, 0.0072m, TimeSpan.FromMilliseconds(800));
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"), triageCost, deepCost);

        await Service().ScanAsync(Now, CancellationToken.None);

        // One meter per billed call, in the escalation pair -- the triage row AND the deep row.
        A.CallTo(() => _llmMetrics.RecordLlmCall(triageCost)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _llmMetrics.RecordLlmCall(deepCost)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ScanAsync_ShouldStillMeterTheCall_WhenTheLedgerWriteThrows()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x"));

        // The ledger write faults, but the meter is an INDEPENDENT sink recorded BEFORE the guarded ledger write -- so
        // export-only spend visibility survives a durable-write fault.
        IAiUsageLedger throwingLedger = A.Fake<IAiUsageLedger>();
        A.CallTo(() => throwingLedger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("ledger DB fault"));

        Func<Task> act = () => Service(ledger: throwingLedger).ScanAsync(Now, CancellationToken.None);

        await act.Should().NotThrowAsync();
        A.CallTo(() => _llmMetrics.RecordLlmCall(_sampleCost)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ScanAsync_ShouldNotMeter_WhenNoLlmCallWasMade()
    {
        await SeedUsageAsync(_operator, 100m, Now); // over the 10 budget -> blocked, no call, empty Costs
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be")); // never reached

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _llmMetrics.RecordLlmCall(A<AiCallCost>._)).MustNotHaveHappened();
    }

    /// <summary>
    /// The scan with its platform-wide spend read forced to FAULT — proves the governor is FAIL-OPEN: a spend-read
    /// blip lets the pass run un-gated (the reviewer still wakes), the deliberate INVERSE of the fail-closed risk gate.
    /// Forwards all ten ctor args. DO NOT "fix" the production counterpart of this override to fail-closed by analogy
    /// to RiskGate — that would pause all agent review (and abort the co-located mechanical route) on any DB hiccup.
    /// </summary>
    private sealed class ThrowingReadService : TriggerEvaluationService
    {
        public ThrowingReadService(
            TradingCopilotDbContext discovery,
            DbContextOptions<TradingCopilotDbContext> options,
            IIndicatorSource indicators,
            INotificationChannel notifications,
            ITriggerReviewer reviewer,
            IReviewEnrichmentSource enrichmentSource,
            IAiUsageLedger ledger,
            ILlmMetrics metrics,
            IAiSpendGovernor governor,
            IOptions<GovernorOptions> governorOptions,
            ILogger<TriggerEvaluationService> logger)
            : base(discovery, options, indicators, notifications, reviewer, enrichmentSource, ledger, metrics, governor, governorOptions, logger)
        {
        }

        protected override Task<decimal> ReadWindowSpendAsync(DateTimeOffset windowStart, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated spend-read fault");
    }

    /// <summary>
    /// The real fail-open <see cref="AiUsageLedger"/> with a forced write fault: its <c>WriteAsync</c> throws, so the
    /// production <c>RecordAsync</c> catch swallows it (fail-open by contract). Composed into the scan to prove a ledger
    /// fault never rolls back a fire -- the honest production shape, not a fake that violates the never-throw contract.
    /// </summary>
    private sealed class ThrowingWriteLedger(DbContextOptions<TradingCopilotDbContext> options)
        : AiUsageLedger(options, NullLogger<AiUsageLedger>.Instance)
    {
        protected override Task WriteAsync(AiUsageEntry entry, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated ledger DB fault");
    }
}
