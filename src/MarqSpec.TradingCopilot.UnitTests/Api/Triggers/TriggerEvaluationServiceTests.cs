using System.Diagnostics;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Suggestions;
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
    private readonly ISessionDeadlineSource _deadlines = A.Fake<ISessionDeadlineSource>();
    private readonly ISuggestionRealtimeNotifier _suggestionNotifier = A.Fake<ISuggestionRealtimeNotifier>();
    private readonly IPriceLevelSource _levels = A.Fake<IPriceLevelSource>();
    private readonly IInstrumentSpecSource _specs = A.Fake<IInstrumentSpecSource>();
    private static DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch.AddYears(56);

    // Every EXISTING test defaults to an INERT confluence (an empty ladder assembles no supporting factors), so the
    // cited-factor set stays the N=1 single primary and every pre-gh#730 case is behaviour-unchanged -- mirroring the
    // inert-governor idiom above. The gh#730 corroboration cases pass a configured ladder + arrange the reads.
    private static readonly ConfluenceOptions _inertConfluence = new() { TimeframeMinutes = [] };

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
        IAiUsageLedger? ledger = null, GovernorOptions? governor = null, IReviewEnrichmentSource? enrichment = null,
        SuggestionOptions? suggestionOptions = null, ConfluenceOptions? confluence = null,
        INotificationChannel? notifications = null) => new(
        Context(), Options, _indicators, notifications ?? _notifications, _reviewer, enrichment ?? _enrichment, ledger ?? _ledger,
        _llmMetrics, _deadlines, Microsoft.Extensions.Options.Options.Create(suggestionOptions ?? new SuggestionOptions()), new AiSpendGovernor(),
        Microsoft.Extensions.Options.Options.Create(governor ?? new GovernorOptions()),
        _suggestionNotifier, new SuggestionThrottle(), _levels, _specs,
        Microsoft.Extensions.Options.Options.Create(confluence ?? _inertConfluence), NullLogger<TriggerEvaluationService>.Instance);

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
        int resolution = Resolution,
        TriggerConfirmation confirmation = TriggerConfirmation.Confirmed,
        DateTimeOffset? unmeasurableSince = null,
        DateTimeOffset? stalenessReportedAt = null)
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
            ResolutionMinutes = resolution,
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
            UnmeasurableSince = unmeasurableSince,
            StalenessReportedAt = stalenessReportedAt,
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

    // --- gh#1045: an indicator staleness report reaches the operator, not just the log (gh#469 / gh#515) ---

    [Fact]
    public async Task ScanAsync_ShouldSendAnAdvisory_WhenAnIndicatorHasBeenUnmeasurablePastTheStalenessThreshold()
    {
        Guid id = await AddTriggerAsync(
            armState: TriggerArmState.Armed,
            unmeasurableSince: Now - TriggerStaleness.ReportAfter, // the outage just crossed the 30-minute line
            stalenessReportedAt: null);
        // No IndicatorReturns configured -> the read yields null (Unmeasurable), same shape as the fail-closed-null test.

        int fires = await Service().ScanAsync(Now, CancellationToken.None);

        fires.Should().Be(0, "an unevaluable trigger never fires");
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n =>
                    n.Severity == NotificationSeverity.Notify && n.DedupKey == $"trigger:{id}:staleness"),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        await using TradingCopilotDbContext reload = Context();
        TriggerRecord trigger = await reload.Triggers.SingleAsync(t => t.Id == id);
        trigger.StalenessReportedAt.Should().Be(Now, "the outage is marked reported so a later pass does not re-page");
    }

    [Fact]
    public async Task ScanAsync_ShouldNotReSendTheAdvisory_WhenTheOutageWasAlreadyReported()
    {
        // Debounce check (gh#1045's acceptance criterion): TriggerStaleness.Track already reports at most once
        // per OPEN outage; this proves that debounce also suppresses the NOTIFICATION, not only the log line.
        await AddTriggerAsync(
            armState: TriggerArmState.Armed,
            unmeasurableSince: Now - TriggerStaleness.ReportAfter - TimeSpan.FromHours(1),
            stalenessReportedAt: Now - TimeSpan.FromMinutes(5)); // already reported earlier in this same outage

        await Service().ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _notifications.SendAsync(A<Notification>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // (review fix) The staleness advisory's dedup key is STATIC per trigger, not per outage, and
    // DedupingNotificationChannel only releases a key via ResolveAsync -- so a reported outage that clears MUST
    // resolve its key, or a later, independent outage on the same trigger is silently swallowed forever.
    [Fact]
    public async Task ScanAsync_ShouldResolveTheStalenessIncident_WhenAReportedOutageRecovers()
    {
        Guid id = await AddTriggerAsync(
            armState: TriggerArmState.Armed,
            unmeasurableSince: Now - TriggerStaleness.ReportAfter - TimeSpan.FromMinutes(5),
            stalenessReportedAt: Now - TimeSpan.FromMinutes(5)); // this outage was already reported
        IndicatorReturns(40m); // measurable and NOT satisfied (Below 30) -> recovery, no fire

        await Service().ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _notifications.ResolveAsync($"trigger:{id}:staleness", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ScanAsync_ShouldNotResolveTheStalenessIncident_WhenAnOutageThatWasNeverReportedRecovers()
    {
        // An outage that never crossed the 30-minute threshold never held the dedup key -- no resolve is needed,
        // and calling it anyway would be a no-op at best but is worth pinning as intentional, not accidental.
        await AddTriggerAsync(
            armState: TriggerArmState.Armed,
            unmeasurableSince: Now - TimeSpan.FromMinutes(5), // short-lived, never reported
            stalenessReportedAt: null);
        IndicatorReturns(40m); // recovers before the threshold

        await Service().ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _notifications.ResolveAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // (review fix, red-then-green against the REAL dedup layer) Without the resolve above, DedupingNotificationChannel
    // -- a process-lifetime singleton -- delivers the FIRST reported outage and then silently drops every LATER,
    // independent outage on the SAME trigger for the rest of the process's uptime: "once per process" instead of
    // "once per outage". This is exercised against the real decorator (not the bare fake every other test in this
    // file uses) because the bug lives entirely in the interaction between this service and that decorator.
    [Fact]
    public async Task ScanAsync_ShouldNotifyAgain_WhenTheTriggerRecoversThenGoesStaleASecondTime()
    {
        INotificationChannel inner = A.Fake<INotificationChannel>();
        A.CallTo(() => inner.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);
        DedupingNotificationChannel deduping = new(inner, NullLogger<DedupingNotificationChannel>.Instance);

        Guid id = await AddTriggerAsync(
            armState: TriggerArmState.Armed,
            unmeasurableSince: Now - TriggerStaleness.ReportAfter, // first outage crosses the line immediately
            stalenessReportedAt: null);

        // Pass 1: crosses the threshold -> the FIRST outage is reported.
        await Service(notifications: deduping).ScanAsync(Now, CancellationToken.None);

        // Pass 2: the indicator recovers (measurable, not satisfied) -- the outage clears.
        IndicatorReturns(40m);
        await Service(notifications: deduping).ScanAsync(Now, CancellationToken.None);

        // A SECOND, independent outage starts well after the first and crosses the threshold on its own.
        DateTimeOffset later = Now.AddDays(1);
        await using (TradingCopilotDbContext mid = Context())
        {
            TriggerRecord trigger = await mid.Triggers.SingleAsync(t => t.Id == id);
            trigger.UnmeasurableSince = later - TriggerStaleness.ReportAfter;
            trigger.StalenessReportedAt = null;
            await mid.SaveChangesAsync();
        }
        IndicatorReturns(null); // unmeasurable again

        // Pass 3: the second outage must notify too -- NOT be swallowed as a stale duplicate of the first.
        await Service(notifications: deduping).ScanAsync(later, CancellationToken.None);

        A.CallTo(() => inner.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == $"trigger:{id}:staleness"),
                A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

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

    // =====================================================================================================
    // gh#766 -- the agent-path trace (ADR-0002): the scan pass is ONE root span; each fired agent-review trigger is
    // a child agent.review span, so a suggestion's trace shows which strategies contributed and where the time went.
    // There is no multi-agent fan-in in the code -- the "agent" is a fired agent-review trigger, the "executor" the
    // sequential scan pass. Instrumentation on the existing OTel stack (gh#230), no new plumbing.
    // =====================================================================================================

    [Fact]
    public async Task ScanAsync_ShouldEmitAnAgentReviewChildSpanPerFiredTrigger_UnderOnePassRoot()
    {
        Guid accountId = await SeedAccountAsync();
        Guid triggerA = await AddTriggerAsync(route: TriggerRoute.AgentReview, threshold: 30m, accountId: accountId, size: 1);
        Guid triggerB = await AddTriggerAsync(route: TriggerRoute.AgentReview, threshold: 40m, accountId: accountId, size: 1);
        IndicatorReturns(25m); // Below both thresholds -- both agent-review triggers fire this pass
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

        List<Activity> spans = [];
        using ActivityListener listener = CaptureSpans(spans);

        await Service().ScanAsync(Now, CancellationToken.None);

        Activity root = spans.Should().ContainSingle(
            span => span.OperationName == "trigger-scan.pass", "the scan pass is one root span").Which;
        List<Activity> agentSpans = spans.Where(span => span.OperationName == "agent.review").ToList();
        agentSpans.Should().HaveCount(2, "each fired agent-review trigger is its own child span");
        agentSpans.Should().OnlyContain(
            span => span.ParentSpanId == root.SpanId,
            "every agent span is a child of the pass root, so one trace shows the whole fan-in");
        agentSpans.Select(span => (string?)span.GetTagItem("trigger.id")).Should().BeEquivalentTo(
            new[] { triggerA.ToString(), triggerB.ToString() },
            "each agent span carries the strategy (trigger) identity");
    }

    [Fact]
    public async Task ScanAsync_ShouldStillEmitTheAgentSpan_WhenTheAgentAbstains()
    {
        // A missing span and a fast span must not look alike (gh#766): an agent that reviews and abstains is still a
        // visible child span, tagged with the outcome that distinguishes it from a proposal.
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suppress(SuppressReason.NotWorthSurfacing, "not worth surfacing"));

        List<Activity> spans = [];
        using ActivityListener listener = CaptureSpans(spans);

        await Service().ScanAsync(Now, CancellationToken.None);

        Activity agentSpan = spans.Should().ContainSingle(
            span => span.OperationName == "agent.review", "an abstaining agent still emits its span").Which;
        agentSpan.GetTagItem("agent.outcome").Should().Be(
            "suppress:NotWorthSurfacing",
            "the span records that the agent reviewed and abstained -- distinguishable from a proposal");
    }

    [Fact]
    public async Task ScanAsync_ShouldLinkTheAgentSpanToTheStagedSuggestion()
    {
        // The attribute that closes the loop (gh#766): when a fire stages a suggestion, its agent span carries the
        // suggestion id, so a trace joins to the row (and, via the ledger's now-populated trace id, to its spend).
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 3);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

        List<Activity> spans = [];
        using ActivityListener listener = CaptureSpans(spans);

        await Service().ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        Suggestion suggestion = await reload.Suggestions.SingleAsync();
        Activity agentSpan = spans.Should().ContainSingle(span => span.OperationName == "agent.review").Which;
        agentSpan.GetTagItem("suggestion.id").Should().Be(
            suggestion.Id.ToString(), "the agent span links to the suggestion the fire produced");
        agentSpan.GetTagItem("agent.outcome").Should().Be("suggest", "the fire proposed a setup");
    }

    // Captures every finished Activity from the app's ActivitySource into `into`. A listener that does not force
    // sampling would let StartActivity return null and prove nothing (the gh#230 test pattern); AllDataAndRecorded
    // forces real Activities. Disposed per test (via `using`) so it never leaks into a sibling.
    private static ActivityListener CaptureSpans(List<Activity> into)
    {
        string sourceName = TelemetryRegistration.Source.Name;
        ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (into) { into.Add(activity); } },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    // (b) The review runs ONCE per arming edge -- a persistently-true condition must not re-review every pass.
    [Fact]
    public async Task ScanAsync_ShouldReviewOncePerArmCycle_NotEveryPass()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

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

    // (e) gh#1042: an incoherent proposal (a Buy with the stop ABOVE entry) is rejected by SuggestionGeometry
    // before persist -- this is the REAL InvalidGeometry failure (the reviewer itself never constructs that
    // SuppressReason; SuggestionGeometry.Validate is the only place it happens), so the operator must be told,
    // not just the engineer reading the log. Regression test: this assertion used to be MustNotHaveHappened.
    [Fact]
    public async Task ScanAsync_ShouldSendAFallbackAdvisory_WhenTheProposedGeometryIsIncoherent()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 105m, 110m, "broken", 72));

        await Service().ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.AnyAsync()).Should().BeFalse();
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n =>
                    n.Severity == NotificationSeverity.Notify
                    && n.Body == "A setup fired that needs agent review, but the reviewer's response could not be used. Review it manually."
                    && !n.Body.Contains("broken", StringComparison.Ordinal)), // never re-surfaces the model's rationale/output
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    // (e2) gh#1042: MalformedOutput -- the reviewer's own fail-closed mapping of an unusable model response --
    // gets the SAME fallback advisory as NoReviewerConfigured / ReviewerUnavailable, not a silent log line.
    [Fact]
    public async Task ScanAsync_ShouldSendAFallbackAdvisory_WhenTheReviewIsMalformedOutput()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suppress(SuppressReason.MalformedOutput, "unknown direction 'sideways'"));

        await Service().ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.AnyAsync()).Should().BeFalse();
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n =>
                    n.Severity == NotificationSeverity.Notify
                    && n.Body == "A setup fired that needs agent review, but the reviewer's response could not be used. Review it manually."
                    // never re-surfaces the model-derived suppress detail (untrusted display data)
                    && !n.Body.Contains("sideways", StringComparison.Ordinal)),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    // (f) An undeclared account mode cannot be traded -- nothing is suggested on it (mode is read live).
    [Fact]
    public async Task ScanAsync_ShouldStageNoSuggestion_WhenTheAccountModeIsUndeclared()
    {
        Guid accountId = await SeedAccountAsync(mode: TradingMode.Undeclared);
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));
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

    // --- gh#684: the realtime suggestion push (issued / superseded), per-owner and best-effort AFTER the commit ---

    // (h1) An issued suggestion is pushed to the OWNING operator (R-20) once it is durable, carrying its id + Active.
    [Fact]
    public async Task ScanAsync_ShouldPushTheIssuedSuggestionToTheOwner_WhenAgentReviewStagesOne()
    {
        Guid accountId = await SeedAccountAsync(mode: TradingMode.Practice);
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

        await Service().ScanAsync(Now, CancellationToken.None);

        Guid issuedId = (await Context().Suggestions.SingleAsync()).Id;
        A.CallTo(() => _suggestionNotifier.SuggestionChangedAsync(
                _operator,
                A<RealtimeSuggestion>.That.Matches(s => s.SuggestionId == issuedId && s.State == "Active"),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    // (h2) A supersede pushes BOTH transitions: the incumbent going ExpiredVoid and the new row arriving Active, so a
    // card surface clears the old and adds the new without a poll. (v1 Active in pass 1; v1 ExpiredVoid + v2 Active in pass 2.)
    [Fact]
    public async Task ScanAsync_ShouldPushBothTheVoidedIncumbentAndTheSupersedingRow_WhenSuperseding()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "v1", 72));
        await Service().ScanAsync(Now, CancellationToken.None);

        await FireAgainAsync(new ReviewOutcome.Suggest(OrderSide.Buy, 101m, 100m, 104m, "v2", 75));

        await using TradingCopilotDbContext reload = Context();
        List<Suggestion> chain = await reload.Suggestions.OrderBy(s => s.Version).ToListAsync();
        Guid v1 = chain[0].Id;
        Guid v2 = chain[1].Id;

        A.CallTo(() => _suggestionNotifier.SuggestionChangedAsync(
                _operator, A<RealtimeSuggestion>.That.Matches(s => s.SuggestionId == v1 && s.State == "Active"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _suggestionNotifier.SuggestionChangedAsync(
                _operator, A<RealtimeSuggestion>.That.Matches(s => s.SuggestionId == v1 && s.State == "ExpiredVoid"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _suggestionNotifier.SuggestionChangedAsync(
                _operator, A<RealtimeSuggestion>.That.Matches(s => s.SuggestionId == v2 && s.State == "Active"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    // (h3) No push when no suggestion is staged -- an incoherent proposal is rejected before persist, nothing to send.
    [Fact]
    public async Task ScanAsync_ShouldNotPushAnySuggestion_WhenNoneIsStaged()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 105m, 110m, "broken", 72)); // stop above entry

        await Service().ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _suggestionNotifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // (h4) The push is best-effort: a throwing notifier must never fail the scan or roll back the durable suggestion.
    // fires == 1 is the discriminator -- an UNguarded throw would unwind to the per-owner catch, losing the count.
    [Fact]
    public async Task ScanAsync_ShouldKeepTheSuggestionAndNotThrow_WhenTheRealtimePushThrows()
    {
        Guid accountId = await SeedAccountAsync();
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 2, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));
        A.CallTo(() => _suggestionNotifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        int fires = await Service().ScanAsync(Now, CancellationToken.None); // must NOT propagate

        fires.Should().Be(1);
        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.CountAsync()).Should().Be(1);            // durable regardless of the best-effort push
        TriggerRecord trigger = await reload.Triggers.SingleAsync(t => t.Id == id);
        trigger.ArmState.Should().Be(TriggerArmState.Fired);
        trigger.LastFiredAt.Should().Be(Now);
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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

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

    // gh#767: the recorded cost and the suggestion it produced share the FIRING id -- the correlation key that later
    // attributes this call's spend to that suggestion. The cost is ledgered BEFORE the suggestion is staged, so the
    // firing (which both the AIUsage row and the Suggestion carry) is the only key that spans them.
    [Fact]
    public async Task ScanAsync_ShouldStampTheRecordedCostWithTheSameFiring_AsTheSuggestionItProduced()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(
            route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

        AiUsageEntry? captured = null;
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._))
            .Invokes((AiUsageEntry entry, CancellationToken _) => captured = entry);

        await Service().ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        Suggestion suggestion = await reload.Suggestions.SingleAsync();
        captured.Should().NotBeNull();
        captured!.TriggerFiringId.Should().NotBeNull();
        captured.TriggerFiringId.Should().Be(suggestion.TriggerFiringId); // the join key that attributes cost to the suggestion
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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72), triageCost, deepCost);

        // A REAL ledger so both spend rows are actually persisted (the fake records nothing to count).
        IAiUsageLedger realLedger = new AiUsageLedger(Options, NullLogger<AiUsageLedger>.Instance);
        await Service(ledger: realLedger).ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        List<AiUsageRecord> rows = await reload.AiUsage.ToListAsync();
        rows.Should().HaveCount(2);                                   // one row per billed call
        rows.Should().OnlyContain(row => row.UserId == _operator);    // both stamped with the firing owner (R-20)

        // gh#767: BOTH the triage and the deep row carry the produced suggestion's firing, so a per-suggestion
        // SUM(cost) totals triage+deep for the one suggestion -- the escalation-correlation the read increment sums.
        Suggestion suggestion = await reload.Suggestions.SingleAsync();
        suggestion.TriggerFiringId.Should().NotBeNull();
        rows.Should().OnlyContain(row => row.TriggerFiringId == suggestion.TriggerFiringId);
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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));
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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));
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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be", 72)); // must never be reached

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

        TriggerEvaluationService service = new ThrowingReadService(
            Context(), Options, _indicators, _notifications, _reviewer, _enrichment, _ledger, _llmMetrics, _deadlines, Microsoft.Extensions.Options.Options.Create(new SuggestionOptions()), new AiSpendGovernor(),
            Microsoft.Extensions.Options.Options.Create(new GovernorOptions { DailyBudgetUsd = 10m }),
            _suggestionNotifier, new SuggestionThrottle(), _levels, _specs,
            Microsoft.Extensions.Options.Options.Create(_inertConfluence), NullLogger<TriggerEvaluationService>.Instance);

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be", 72)); // must never be reached

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72)); // Cost defaults to _sampleCost

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72), triageCost, deepCost);

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be", 72)); // must never be reached

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be", 72));

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be", 72)); // must never be reached

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustNotHaveHappened();
        A.CallTo(() => _enrichment.BuildAsync(A<TriggerReviewContext>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    // =====================================================================================================
    // ISSUANCE FIELDS: the rationale + cited signal (gh#542), the confidence (gh#543) and the validity window
    // (gh#544) are stamped at staging. Size, mode and expiry are the SYSTEM's; only the prose and the number
    // come from the model.
    // =====================================================================================================

    private async Task<Suggestion> StageAndReadAsync(int confidence = 72, string rationale = "oversold bounce")
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, rationale, confidence));

        await Service().ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        return await reload.Suggestions.Include(suggestion => suggestion.CitedFactors).SingleAsync();
    }

    [Fact]
    public async Task ScanAsync_ShouldPersistTheRationaleAndCiteOnePrimaryIndicatorFactor_CopiedFromTheTrigger()
    {
        // gh#729/ADR-0026: issuance stages the N=1 cited-factor set — ONE primary Indicator factor copied from the
        // fired trigger, ZERO supporting (assembly across timeframes/levels is gated on gh#595). COPIED, not joined:
        // indicator/period/resolution live on the mutable, deletable TriggerRecord, so the factor is snapshotted to
        // stay readable after the trigger is edited or deleted. IsPrimary is derived by the min-rule, never hand-set.
        Suggestion staged = await StageAndReadAsync(rationale: "reclaimed the band on rising delta");

        staged.Rationale.Should().Be("reclaimed the band on rising delta");
        staged.TriggerFiringId.Should().NotBeNull();

        CitedFactor primary = staged.CitedFactors.Should().ContainSingle().Subject;
        primary.IsPrimary.Should().BeTrue("the set of one is its own primary");
        primary.Kind.Should().Be(CitedFactorKind.Indicator);
        primary.Indicator.Should().Be(Indicator);
        primary.Period.Should().Be(Period);
        primary.TimeframeMinutes.Should().Be(Resolution, "the primary's timeframe is the fired bar size (old CitedResolutionMinutes)");
        primary.LevelId.Should().BeNull("today's issuance cites no level factor");
        staged.CitedFactors.Count(factor => !factor.IsPrimary).Should().Be(0, "no supporting factors are assembled yet");
    }

    // =====================================================================================================
    // CONFLUENCE ASSEMBLY (gh#730, ADR-0026, R-4): at issuance, the fired signal (the primary) is corroborated
    // against the SAME signal on the other ladder timeframes and the active levels near the entry, each appended as a
    // SUPPORTING factor. A level never fires, so it is never primary. A confluence read fault falls back to today's
    // N=1 single primary (fail-open) -- it must never abort the owner's pass or the fire.
    // =====================================================================================================

    // A contract spec that resolves a tick size, so the level proximity band can be measured.
    private static InstrumentContractSpec SpecFor(decimal tickSize) =>
        new(InstrumentSpec.Create(InstrumentId.Parse(Symbol), tickSize, 50m), 40);

    // One active level zone on a timeframe -- the shape the venue-agnostic level read returns.
    private static PriceLevel LevelZone(int timeframe, decimal bottom, decimal top) => new()
    {
        Id = Guid.NewGuid(),
        Venue = "TOPSTEPX",
        Instrument = Symbol,
        TimeframeMinutes = timeframe,
        Top = top,
        Bottom = bottom,
        Kind = PriceLevelKind.Support,
        Significance = 5m,
        FormedAtBucket = Now,
        TouchCount = 2,
        Active = true,
        UpdatedAt = Now,
    };

    [Fact]
    public async Task ScanAsync_ShouldStageAPrimaryPlusSupportingIndicatorAndLevel_WhenConfluenceCorroborates()
    {
        // The fired trigger is RSI(14) on the 1m (Resolution), BELOW 30. The ladder adds a 15m (which corroborates)
        // and a 60m (which does not), plus an active 15m level the entry sits inside the band of.
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        ConfluenceOptions confluence = new() { KTicks = 8, FAtr = 0.5m, TimeframeMinutes = [15, 60] };

        IndicatorReturns(25m); // the 1m fire read AND the 15m ladder read -> both satisfied (below 30)
        A.CallTo(() => _indicators.GetValueAsync(
                A<InstrumentId>._, A<string>._, A<int>._, 60, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns(40m); // the 60m does NOT satisfy -> excluded

        // The tick size resolves (band tick-arm = 8 * 0.25 = 2.0); ATR is unmeasured (null) so the tick arm stands.
        InstrumentContractSpec? outSpec = SpecFor(0.25m);
        A.CallTo(() => _specs.TryResolve(A<InstrumentId>._, out outSpec)).Returns(true);
        IReadOnlyList<PriceLevel> activeLevels = [LevelZone(15, 98m, 99m)]; // entry 100 is 1.0 from the near edge <= 2.0
        A.CallTo(() => _levels.GetActiveLevelsAsync(A<string>._, A<IReadOnlyCollection<int>>._, A<CancellationToken>._))
            .Returns(activeLevels);

        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

        await Service(confluence: confluence).ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        Suggestion suggestion = await reload.Suggestions.Include(s => s.CitedFactors).SingleAsync();

        suggestion.CitedFactors.Should().HaveCount(3, "one primary indicator + one supporting indicator + one supporting level");
        suggestion.CitedFactors.Count(f => f.IsPrimary).Should().Be(1, "exactly one primary (ADR-0026)");

        CitedFactor primary = suggestion.CitedFactors.Single(f => f.IsPrimary);
        primary.Kind.Should().Be(CitedFactorKind.Indicator);
        primary.TimeframeMinutes.Should().Be(Resolution, "the fired smallest timeframe is the headline (gh#592)");

        CitedFactor supportingIndicator = suggestion.CitedFactors
            .Single(f => !f.IsPrimary && f.Kind == CitedFactorKind.Indicator);
        supportingIndicator.TimeframeMinutes.Should().Be(15, "the 15m read the SAME signal");
        supportingIndicator.Indicator.Should().Be(Indicator);
        supportingIndicator.Period.Should().Be(Period);

        CitedFactor level = suggestion.CitedFactors.Single(f => f.Kind == CitedFactorKind.Level);
        level.IsPrimary.Should().BeFalse("a level never fires, so it is never primary (ADR-0026)");
        level.TimeframeMinutes.Should().Be(15);
        level.LevelTop.Should().Be(99m);
        level.LevelBottom.Should().Be(98m);
        level.LevelVenue.Should().Be("TOPSTEPX");
        level.LevelId.Should().NotBeNull("the snapshot carries the soft level id");
    }

    [Fact]
    public async Task ScanAsync_ShouldKeepTheFiredSignalPrimary_WhenALowerLadderRungAlsoSatisfies()
    {
        // REGRESSION (review #968): the fired trigger is a 60m SWING; the ladder also carries a LOWER 15m rung that
        // reads the SAME signal satisfied. Corroboration is HIGHER-timeframe only (ADR-0026 §3 "the larger ones are
        // supporting"), so the fired 60m stays the primary/headline (§2) and the lower 15m is NOT cited at all -- a
        // lower rung must never steal the headline and rebrand a 60m swing as a 15m scalp (R-4 headline, R-9 journal).
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(
            route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed, resolution: 60);
        ConfluenceOptions confluence = new() { TimeframeMinutes = [15, 60, 240] };

        IndicatorReturns(25m); // the 60m fire, the lower 15m AND the higher 240m all read below 30 (all satisfied)

        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

        await Service(confluence: confluence).ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        Suggestion suggestion = await reload.Suggestions.Include(s => s.CitedFactors).SingleAsync();

        suggestion.CitedFactors.Count(f => f.IsPrimary).Should().Be(1, "exactly one primary (ADR-0026)");
        suggestion.CitedFactors.Single(f => f.IsPrimary).TimeframeMinutes
            .Should().Be(60, "the FIRED signal is the headline; a lower rung never steals primary (ADR-0026 §2)");

        // Only the fired 60m (primary) and the HIGHER 240m (supporting) are cited -- the lower 15m is never read.
        suggestion.CitedFactors
            .Where(f => f.Kind == CitedFactorKind.Indicator)
            .Select(f => f.TimeframeMinutes)
            .Should().BeEquivalentTo(new[] { 60, 240 }, "corroboration is higher-timeframe only; the lower 15m is excluded");
    }

    [Fact]
    public async Task ScanAsync_ShouldStageTheSingleN1Factor_WhenConfluenceFindsNoCorroboration()
    {
        // A real ladder is configured, but nothing corroborates: the other timeframes cannot be measured (null) and
        // no instrument spec resolves, so no level is read. The set degrades to today's N=1 single primary factor.
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        ConfluenceOptions confluence = new() { TimeframeMinutes = [15, 60] };

        IndicatorReturns(25m); // the 1m fire read is satisfied...
        A.CallTo(() => _indicators.GetValueAsync(
                A<InstrumentId>._, A<string>._, A<int>._, 15, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns((decimal?)null); // ...but the ladder timeframes cannot be measured -> no corroboration
        A.CallTo(() => _indicators.GetValueAsync(
                A<InstrumentId>._, A<string>._, A<int>._, 60, A<DateTimeOffset>._, A<CancellationToken>._))
            .Returns((decimal?)null);
        // No spec resolves (the fake's default TryResolve is false) -> no level read at all.

        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

        await Service(confluence: confluence).ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        Suggestion suggestion = await reload.Suggestions.Include(s => s.CitedFactors).SingleAsync();
        CitedFactor only = suggestion.CitedFactors.Should().ContainSingle().Subject;
        only.IsPrimary.Should().BeTrue("the set of one is its own primary");
        only.Kind.Should().Be(CitedFactorKind.Indicator);
        only.TimeframeMinutes.Should().Be(Resolution);
    }

    [Fact]
    public async Task ScanAsync_ShouldFallBackToN1_WhenAConfluenceReadFaults()
    {
        // FAIL-OPEN (mirrors the enrichment + governor reads): a confluence read fault must NEVER abort the fire. The
        // 15m indicator WOULD corroborate, but the level read throws, so the WHOLE assembly degrades to the N=1 set --
        // and fires == 1 proves the throw was contained, not unwound to the per-owner guard.
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        ConfluenceOptions confluence = new() { TimeframeMinutes = [15] };

        IndicatorReturns(25m); // the 1m fire + the 15m ladder read both satisfied (would corroborate)
        InstrumentContractSpec? outSpec = SpecFor(0.25m);
        A.CallTo(() => _specs.TryResolve(A<InstrumentId>._, out outSpec)).Returns(true);
        A.CallTo(() => _levels.GetActiveLevelsAsync(A<string>._, A<IReadOnlyCollection<int>>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("levels store down"));

        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

        int fires = await Service(confluence: confluence).ScanAsync(Now, CancellationToken.None); // must NOT propagate

        fires.Should().Be(1, "a confluence read fault is contained, never unwound to abort the pass");
        await using TradingCopilotDbContext reload = Context();
        Suggestion suggestion = await reload.Suggestions.Include(s => s.CitedFactors).SingleAsync();
        suggestion.CitedFactors.Should().ContainSingle().Which.IsPrimary.Should().BeTrue("fail-open to the N=1 set");
    }

    [Fact]
    public async Task ScanAsync_ShouldLinkTheSuggestionToTheFiringItCameFrom()
    {
        Suggestion staged = await StageAndReadAsync();

        await using TradingCopilotDbContext reload = Context();
        TriggerFiringRecord firing = await reload.TriggerFirings.SingleAsync();
        staged.TriggerFiringId.Should().Be(firing.Id);
    }

    [Fact]
    public async Task ScanAsync_ShouldPersistTheConfidence()
    {
        (await StageAndReadAsync(confidence: 81)).Confidence.Should().Be(81);
    }

    [Fact]
    public async Task ScanAsync_ShouldStillStageAndKeepTheOperatorsSize_WhenConfidenceIsZero()
    {
        // The display-only invariant, at the issuance boundary: a low number changes NOTHING. The row is still
        // written, and Size still comes from the operator's trigger rather than being scaled by the model's opinion.
        Suggestion staged = await StageAndReadAsync(confidence: 0);

        staged.Confidence.Should().Be(0);
        staged.Size.Should().Be(3, "size is the operator's trigger's, never the model's");
    }

    [Fact]
    public async Task ScanAsync_ShouldStampAValidityWindow_ClampedByTheSessionDeadline()
    {
        // The scan's fake deadline source returns null by default, so the configured window stands unclamped and the
        // expiry is simply CreatedAt + validity. The clamp itself is proven against SuggestionValidity's own suite.
        Suggestion staged = await StageAndReadAsync();

        staged.ExpiresAt.Should().Be(staged.CreatedAt.Add(new SuggestionOptions().Validity));
        staged.ExpiresAt.Should().BeAfter(staged.CreatedAt, "the DB CHECK requires it");
    }

    [Fact]
    public async Task ScanAsync_ShouldTellTheOperatorWhenTheSuggestionExpires()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

        await Service().ScanAsync(Now, CancellationToken.None);

        // The window reaches the operator on the channel that already reaches them, not only in the app.
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.Body.Contains("Valid until") && n.Body.Contains("CT")),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72)); // one billed triage call (_sampleCost)

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72), triageCost, deepCost);

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

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
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "would-be", 72)); // never reached

        await Service(governor: new GovernorOptions { DailyBudgetUsd = 10m }).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _llmMetrics.RecordLlmCall(A<AiCallCost>._)).MustNotHaveHappened();
    }

    // =====================================================================================================
    // SUPERSEDE (gh#550, R-4, ADR-0013): a re-formed setup issues a NEW, superseding suggestion rather than
    // resurrecting the prior one. When the SAME trigger + instrument + side re-arms and fires again, the live,
    // undispositioned incumbent is voided (ExpiredVoid) and the new row is linked to it (SupersedesId, Version+1),
    // all inside the scan's shared transaction. Keyed on the TRIGGER identity (the incumbent's originating firing's
    // TriggerId), never the symbol -- so two different triggers on one symbol+side never destroy each other.
    // =====================================================================================================

    // Drives one arming edge: re-arm (indicator not satisfied), then fire (indicator satisfied) with the given
    // proposal. The first call in a test can skip the re-arm by starting from an Armed trigger.
    private async Task FireAgainAsync(ReviewOutcome.Suggest proposal)
    {
        IndicatorReturns(40m); // above the below-30 line -> NotSatisfied -> re-arm + bump the cycle
        await Service().ScanAsync(Now, CancellationToken.None);
        IndicatorReturns(25m); // satisfied again -> a fresh crossing fires under the new cycle
        ReviewerReturns(proposal);
        await Service().ScanAsync(Now, CancellationToken.None);
    }

    // (a) A re-arm of the SAME trigger+instrument+side supersedes the incumbent and links the new row.
    [Fact]
    public async Task ScanAsync_ShouldSupersedeTheIncumbentAndLinkTheNewRow_WhenTheSameTriggerReArmsAndFiresAgain()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "v1", 72));
        await Service().ScanAsync(Now, CancellationToken.None); // stage v1 (Active)

        await FireAgainAsync(new ReviewOutcome.Suggest(OrderSide.Buy, 101m, 100m, 104m, "v2", 75)); // stage v2, supersede v1

        await using TradingCopilotDbContext reload = Context();
        List<Suggestion> chain = await reload.Suggestions.OrderBy(s => s.Version).ToListAsync();
        chain.Should().HaveCount(2);

        Suggestion v1 = chain[0];
        Suggestion v2 = chain[1];
        v1.Version.Should().Be(1);
        v1.State.Should().Be(SuggestionState.ExpiredVoid, "the incumbent is superseded, reusing ExpiredVoid (gh#550)");
        v1.SupersedesId.Should().BeNull("the first version supersedes nothing");
        v2.Version.Should().Be(2, "the superseding row is the next version");
        v2.State.Should().Be(SuggestionState.Active);
        v2.SupersedesId.Should().Be(v1.Id, "the new row links to the incumbent it superseded");
    }

    // (b) Two DIFFERENT triggers on the same symbol+side coexist -- neither supersedes the other (keyed on the
    // trigger IDENTITY, NOT the symbol). The incumbent from trigger A must be a COMMITTED row when trigger B fires,
    // or the test proves nothing (gh#550 review): B's incumbent query is a DB read, and a suggestion .Add-ed earlier
    // in the SAME pass is unsaved and invisible to it -- so a single-pass version passes GREEN even with the
    // trigger-identity clause deleted. Here A commits in pass 1 and B fires in pass 2, so B's query DOES see A by
    // (instrument, side); the firing-link clause (firing.TriggerId == B) is what stops it voiding A. Delete that
    // clause and B supersedes A -> this goes red. That is the guard the whole "key on the trigger" thesis needs.
    [Fact]
    public async Task ScanAsync_ShouldNotSupersede_WhenTwoDifferentTriggersShareSymbolAndSide()
    {
        Guid accountId = await SeedAccountAsync();
        // Same symbol (ES) and side (Buy), distinct triggers -- different thresholds = genuinely different setups.
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, threshold: 30m, armState: TriggerArmState.Armed);
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, threshold: 28m, armState: TriggerArmState.Armed);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "x", 72));

        // Pass 1: 29 is below trigger A's 30 but not B's 28 -> ONLY A fires, and its suggestion COMMITS.
        IndicatorReturns(29m);
        await Service().ScanAsync(Now, CancellationToken.None);
        (await Context().Suggestions.CountAsync()).Should().Be(1, "only trigger A fired in pass 1");

        // Pass 2: 25 is below B's 28 -> B fires (A is already Fired and never re-armed, so it does not re-fire). B's
        // incumbent query now SEES the committed A row by (instrument, side) -- and must still not void it, because
        // A's firing carries a different TriggerId.
        IndicatorReturns(25m);
        await Service().ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        List<Suggestion> all = await reload.Suggestions.ToListAsync();
        all.Should().HaveCount(2);
        all.Should().OnlyContain(s => s.State == SuggestionState.Active, "different triggers never supersede each other");
        all.Should().OnlyContain(s => s.SupersedesId == null);
        all.Should().OnlyContain(s => s.Version == 1);
    }

    // (b2) A STALE incumbent (not only an Active one) is superseded too -- the query pins {Active, Stale}. Stale is
    // set by the gh#546 drift sweep, which is not in this base, so simulate the drift: fire once (v1 Active), mark it
    // Stale, then re-fire the same trigger. Without "Stale" in the query's state filter this goes red.
    [Fact]
    public async Task ScanAsync_ShouldSupersedeAStaleIncumbent_NotOnlyAnActiveOne()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "v1", 72));
        await Service().ScanAsync(Now, CancellationToken.None); // stage v1 (Active)

        Guid v1Id = (await Context().Suggestions.SingleAsync()).Id;
        await using (TradingCopilotDbContext drift = Context())
        {
            Suggestion v1 = await drift.Suggestions.SingleAsync();
            v1.State = SuggestionState.Stale; // the gh#546 drift, simulated -- an incumbent can be Stale, not only Active
            await drift.SaveChangesAsync();
        }

        await FireAgainAsync(new ReviewOutcome.Suggest(OrderSide.Buy, 101m, 100m, 104m, "v2", 75));

        await using TradingCopilotDbContext reload = Context();
        Suggestion superseded = await reload.Suggestions.SingleAsync(s => s.Id == v1Id);
        superseded.State.Should().Be(SuggestionState.ExpiredVoid, "a STALE incumbent is superseded too, not only an Active one");
        Suggestion v2 = await reload.Suggestions.SingleAsync(s => s.Id != v1Id);
        v2.Version.Should().Be(2);
        v2.SupersedesId.Should().Be(v1Id);
    }

    // (c) An already-DISPOSITIONED incumbent is NOT voided -- it is journal evidence the operator acted on. The
    // re-fire issues a NEW, independent row (Version 1, supersedes nothing) instead.
    [Fact]
    public async Task ScanAsync_ShouldNotVoidADispositionedIncumbent_AndIssueANewIndependentRow()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 2, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "v1", 72));
        await Service().ScanAsync(Now, CancellationToken.None); // stage v1

        Guid v1Id = (await Context().Suggestions.SingleAsync()).Id;
        await using (TradingCopilotDbContext dispose = Context())
        {
            dispose.SuggestionDispositions.Add(new SuggestionDisposition
            {
                Id = Guid.NewGuid(),
                UserId = _operator,
                SuggestionId = v1Id,
                Kind = SuggestionDispositionKind.Passed,
                Reasons = SuggestionPassReason.None,
                CreatedAt = Now,
            });
            await dispose.SaveChangesAsync();
        }

        await FireAgainAsync(new ReviewOutcome.Suggest(OrderSide.Buy, 101m, 100m, 104m, "v2", 75));

        await using TradingCopilotDbContext reload = Context();
        Suggestion v1 = await reload.Suggestions.SingleAsync(s => s.Id == v1Id);
        v1.State.Should().Be(SuggestionState.Active, "a dispositioned incumbent is journal evidence -- never voided");

        Suggestion v2 = await reload.Suggestions.SingleAsync(s => s.Id != v1Id);
        v2.SupersedesId.Should().BeNull("it did not supersede the dispositioned incumbent");
        v2.Version.Should().Be(1, "a new independent chain, not a continuation");
        v2.State.Should().Be(SuggestionState.Active);
    }

    // (d) Version increments monotonically along a chain: v1 -> v2 -> v3, each superseding the last.
    [Fact]
    public async Task ScanAsync_ShouldIncrementVersionMonotonically_AlongASupersedeChain()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "v1", 72));
        await Service().ScanAsync(Now, CancellationToken.None); // v1

        await FireAgainAsync(new ReviewOutcome.Suggest(OrderSide.Buy, 101m, 100m, 104m, "v2", 72)); // v2
        await FireAgainAsync(new ReviewOutcome.Suggest(OrderSide.Buy, 102m, 101m, 105m, "v3", 72)); // v3

        await using TradingCopilotDbContext reload = Context();
        List<Suggestion> chain = await reload.Suggestions.OrderBy(s => s.Version).ToListAsync();
        chain.Select(s => s.Version).Should().Equal(new[] { 1, 2, 3 }, "each re-arm mints the next version");
        chain.Select(s => s.State).Should().Equal(
            new[] { SuggestionState.ExpiredVoid, SuggestionState.ExpiredVoid, SuggestionState.Active },
            "only the head of the chain stays actionable");
        chain[1].SupersedesId.Should().Be(chain[0].Id);
        chain[2].SupersedesId.Should().Be(chain[1].Id);
    }

    // (e) Superseding NEVER mutates the incumbent's trade parameters -- only its lifecycle state. Immutability of an
    // issued suggestion is the invariant the journal depends on.
    [Fact]
    public async Task ScanAsync_ShouldNotMutateTheIncumbentsTradeParameters_WhenSuperseding()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 3, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "v1", 72));
        await Service().ScanAsync(Now, CancellationToken.None);
        Guid v1Id = (await Context().Suggestions.SingleAsync()).Id;

        // A DELIBERATELY different geometry on the superseding row -- if supersede touched the incumbent's parameters
        // it would show here.
        await FireAgainAsync(new ReviewOutcome.Suggest(OrderSide.Buy, 200m, 190m, 230m, "v2", 40));

        await using TradingCopilotDbContext reload = Context();
        Suggestion v1 = await reload.Suggestions.SingleAsync(s => s.Id == v1Id);
        v1.State.Should().Be(SuggestionState.ExpiredVoid, "it was superseded -- the anchor that a supersede happened");
        v1.EntryPrice.Should().Be(100m, "trade parameters are immutable once issued");
        v1.StopPrice.Should().Be(99m);
        v1.TargetPrice.Should().Be(103m);
        v1.Size.Should().Be(3);
        v1.Side.Should().Be(OrderSide.Buy);
    }

    // (f) A SIDE-FLIP re-arm of the SAME trigger issues an INDEPENDENT row, never a supersede -- side is part of the
    // incumbent key (`candidate.Side == suggest.Side`). The reviewer proposes the OPPOSITE direction on the next
    // firing; the live Buy incumbent is a genuinely different setup (a long and a short are not two versions of one
    // idea) and must be left actionable. This is the guard the "side is part of the key" design point (PR body,
    // ADR-0013, data dictionary) had none of -- every other supersede test uses Buy exclusively (gh#610 review).
    // Delete or invert the `candidate.Side == suggest.Side` clause in StageSuggestionAsync and this goes RED: the
    // Sell re-fire would then find the Buy incumbent by (instrument, trigger) and void it.
    [Fact]
    public async Task ScanAsync_ShouldNotSupersede_WhenTheSameTriggerReArmsWithAFlippedSide()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "long", 72));
        await Service().ScanAsync(Now, CancellationToken.None); // stage a BUY incumbent (Active)
        Guid buyId = (await Context().Suggestions.SingleAsync()).Id;

        // The SAME trigger re-arms and fires again, but the reviewer now proposes a SELL -- geometry inverted for a
        // short (stop above entry, target below), so it clears SuggestionGeometry as a coherent proposal.
        await FireAgainAsync(new ReviewOutcome.Suggest(OrderSide.Sell, 100m, 101m, 97m, "short", 70));

        await using TradingCopilotDbContext reload = Context();
        Suggestion buy = await reload.Suggestions.SingleAsync(s => s.Id == buyId);
        buy.State.Should().Be(SuggestionState.Active, "an opposite-side proposal is a different setup -- the live Buy is left untouched");
        buy.SupersedesId.Should().BeNull();

        Suggestion sell = await reload.Suggestions.SingleAsync(s => s.Id != buyId);
        sell.Side.Should().Be(OrderSide.Sell);
        sell.SupersedesId.Should().BeNull("a side-flip issues an INDEPENDENT row, not a supersede");
        sell.Version.Should().Be(1, "an independent chain starts at version 1, superseding nothing");
    }

    // =====================================================================================================
    // R-4 SUGGESTION THROTTLE (gh#551, ADR-0007/ADR-0008): the scan consults the pure SuggestionThrottle (gh#588)
    // BEFORE it wakes the reviewer, fed the account's daily-drawdown headroom + daily-target state read from THIS
    // owner's own R-20-filtered context. As headroom depletes it issues fewer, higher-conviction suggestions; at the
    // governor or the daily-target stand-down it SUPPRESSES new entries (advisory, ahead of the execution gate). It
    // can only ever REDUCE or SUPPRESS issuance -- never increase it -- and a model-authored confidence can never lift
    // the headroom-derived cap. Inert until SuggestionOptions.ThrottleEnabled, so every test above is unchanged.
    // =====================================================================================================

    // A SuggestionOptions with the throttle turned ON. FullWindowCap defaults to 1 here so a Throttled decision's cap is
    // deterministically 1 (max(1, round(1 * bandFraction)) == 1 for any in-band headroom) -- a cap test can seed exactly
    // one prior suggestion and assert the next is refused without depending on the linear cap-scaling arithmetic.
    private static SuggestionOptions ThrottleOptions(
        decimal thresholdFraction = 0.5m, int fullWindowCap = 1, int convictionFloor = 70) =>
        new()
        {
            ThrottleEnabled = true,
            ThrottleThresholdFraction = thresholdFraction,
            ThrottleFullWindowCap = fullWindowCap,
            ThrottleConvictionFloor = convictionFloor,
        };

    // Seeds the one RiskProfile row per account the throttle reads its governor / target / stand-down from. Only the
    // four fields the throttle consults vary; the rest are valid filler (the in-memory provider enforces no DB checks).
    private async Task SeedRiskProfileAsync(
        Guid accountId,
        decimal governor = 1_000m,
        decimal? dailyProfitTarget = null,
        bool stopForDayAtProfitTarget = false,
        decimal? dailyLossLimit = null,
        Guid? owner = null)
    {
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.RiskProfiles.Add(new RiskProfileRecord
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            AccountId = accountId,
            StartingBalance = 50_000m,
            DailyLossLimit = dailyLossLimit,
            FloorSource = FloorSource.FirmImposed,
            TrailingMode = TrailingMode.EndOfDay,
            TrailingAmount = 2_000m,
            PerTradeRiskFraction = 0.1m,
            TargetRewardRatio = 1.5m,
            MaxDrawdownPerTrade = 300m,
            DailyDrawdownGovernor = governor,
            DailyProfitTarget = dailyProfitTarget,
            StopForDayAtProfitTarget = stopForDayAtProfitTarget,
            SizingBasis = SizingBasis.ActualStop,
            MaxContractsPerOrder = 0,
        });
        await context.SaveChangesAsync();
    }

    // Seeds a CLOSED trade whose realized P&L feeds the day's loss/profit the headroom read consumes: positive = profit,
    // negative = loss. Closed at Now, so it lands in the same Central trading day the throttle counts issuance over.
    private async Task SeedClosedTradeAsync(
        Guid accountId, decimal realizedPnL, Guid? owner = null, TradingMode mode = TradingMode.Practice)
    {
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            AccountId = accountId,
            Instrument = "CON.F.US.ES.U26",
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 100m,
            ExitPrice = 100m + realizedPnL,
            RealizedPnL = realizedPnL,
            Mode = mode,
            ClosedAt = Now,
        });
        await context.SaveChangesAsync();
    }

    // Seeds one existing ACTIVE suggestion for the account today, so a throttled window's issued-count is non-zero.
    // The PRODUCER is a parameter (gh#1134): the cap counts the scan's own issuance, so which producer wrote the row
    // is the difference test (j) turns on.
    private async Task SeedSuggestionAsync(
        Guid accountId, Guid? owner = null, SuggestionOrigin origin = SuggestionOrigin.Scan)
    {
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Suggestions.Add(new Suggestion
        {
            Origin = origin,
            Id = Guid.NewGuid(),
            UserId = ownerId,
            AccountId = accountId,
            Instrument = Symbol,
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 100m,
            StopPrice = 99m,
            TargetPrice = 103m,
            Mode = TradingMode.Practice,
            State = SuggestionState.Active,
            CreatedAt = Now,
            Rationale = "seeded incumbent",
            CitedFactors =
            [
                new CitedFactor
                {
                    Id = Guid.NewGuid(),
                    UserId = ownerId,
                    Kind = CitedFactorKind.Indicator,
                    IsPrimary = true,
                    TimeframeMinutes = Resolution,
                    Indicator = Indicator,
                    Period = Period,
                },
            ],
            Confidence = 80,
            ExpiresAt = Now.AddHours(1),
        });
        await context.SaveChangesAsync();
    }

    // (a) OPT-OUT NO-OP: the flag is the master switch. Even with a fully blown governor (a full day's loss seeded),
    // a DISABLED throttle proposes exactly as before -- proving nothing in the wiring fires until an operator opts in.
    [Fact]
    public async Task ScanAsync_ShouldProposeNormally_WhenTheThrottleIsDisabled()
    {
        Guid accountId = await SeedAccountAsync();
        await SeedRiskProfileAsync(accountId, governor: 1_000m);
        await SeedClosedTradeAsync(accountId, realizedPnL: -1_000m); // governor fully spent -- but the throttle is OFF
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

        await Service().ScanAsync(Now, CancellationToken.None); // default SuggestionOptions => ThrottleEnabled false => inert

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        (await Context().Suggestions.CountAsync()).Should().Be(1);
    }

    // (b) NO GOVERNOR TO THROTTLE AGAINST: enabled, but the account declared no RiskProfile -> inert (FullThrottle), the
    // same posture as an account that set no limit. Proposes normally.
    [Fact]
    public async Task ScanAsync_ShouldProposeNormally_WhenEnabledButTheAccountHasNoRiskProfile()
    {
        Guid accountId = await SeedAccountAsync();
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

        await Service(suggestionOptions: ThrottleOptions()).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        (await Context().Suggestions.CountAsync()).Should().Be(1);
    }

    // (c) HEALTHY HEADROOM -> FULL: enabled, a declared governor, and a GREEN day (no realized loss) -> headroom == 1,
    // at/above the threshold, so the regime is Full and issuance is unthrottled. Proposes normally.
    [Fact]
    public async Task ScanAsync_ShouldProposeNormally_WhenHeadroomIsHealthy()
    {
        Guid accountId = await SeedAccountAsync();
        await SeedRiskProfileAsync(accountId, governor: 1_000m); // no seeded loss => dayLoss 0 => headroom 1.0 => Full
        await SeedSuggestionAsync(accountId);                    // the window already holds one (would exceed a throttled cap of 1)
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

        await Service(suggestionOptions: ThrottleOptions()).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        // Full SKIPS the cap count, so the new suggestion stages despite the window already holding one (2 total). Had
        // healthy headroom been mis-decided as Throttled (FullWindowCap 1), the cap would have refused it, leaving 1 —
        // so this distinguishes Full from a lenient Throttled, not merely "did not suppress".
        (await Context().Suggestions.CountAsync()).Should().Be(2);
    }

    // (d) GOVERNOR REACHED -> SUPPRESS BEFORE SPEND: the day's realized loss equals the governor, so headroom is 0. The
    // reviewer is NOT called (no LLM call, no spend row), NOTHING is suggested, yet a fire is still a fire (journaled +
    // debounced to Fired) and the operator gets exactly one "Suggestions paused" advisory. The safety-critical shape:
    // the throttle caps WHETHER a call happens, one layer ahead of and cheaper than the AI-spend governor.
    [Fact]
    public async Task ScanAsync_ShouldSuppressBeforeSpendAndAdvise_WhenTheGovernorIsReached()
    {
        Guid accountId = await SeedAccountAsync();
        await SeedRiskProfileAsync(accountId, governor: 1_000m);
        await SeedClosedTradeAsync(accountId, realizedPnL: -1_000m); // dayLoss 1000 == governor => headroom 0
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "must never be reached", 99));

        await Service(suggestionOptions: ThrottleOptions()).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustNotHaveHappened();
        A.CallTo(() => _ledger.RecordAsync(A<AiUsageEntry>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.AiUsage.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();   // no call => no spend row
        (await reload.Suggestions.AnyAsync()).Should().BeFalse();                    // nothing issued
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();          // a fire is still a fire
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.Severity == NotificationSeverity.Notify && n.Title.Contains("Suggestions paused")),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    // (e) DAILY-TARGET STAND-DOWN -> SUPPRESS, INDEPENDENT OF HEADROOM: headroom is fully healthy (no loss), but the
    // daily profit target is hit and stand-down is ON -- the R-5 consistency discipline. Suppressed before the reviewer,
    // with a distinct "standing down" advisory, proving the stand-down is not a headroom effect.
    [Fact]
    public async Task ScanAsync_ShouldSuppressBeforeSpendAndAdvise_WhenTheDailyTargetStandDownIsOn()
    {
        Guid accountId = await SeedAccountAsync();
        await SeedRiskProfileAsync(accountId, governor: 1_000m, dailyProfitTarget: 500m, stopForDayAtProfitTarget: true);
        await SeedClosedTradeAsync(accountId, realizedPnL: 600m); // green day AND past the 500 target => stand down
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "must never be reached", 99));

        await Service(suggestionOptions: ThrottleOptions()).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.AnyAsync()).Should().BeFalse();
        (await reload.AiUsage.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();          // no call => no spend row
        (await reload.TriggerFirings.AnyAsync(f => f.TriggerId == id)).Should().BeTrue();    // a fire is still a fire
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
        A.CallTo(() => _notifications.SendAsync(
                A<Notification>.That.Matches(n => n.Title.Contains("Suggestions paused") && n.Body.Contains("standing down")),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    // (f) TARGET REACHED but STAND-DOWN OFF -> PROPOSE: the same green-past-target day, but the operator did not enable
    // stand-down. Pins that DecideThrottleAsync feeds the RAW target-reached (not pre-ANDed with the flag) so the policy
    // applies the flag itself -- otherwise this day would wrongly suppress.
    [Fact]
    public async Task ScanAsync_ShouldProposeNormally_WhenTheDailyTargetIsReachedButStandDownIsOff()
    {
        Guid accountId = await SeedAccountAsync();
        await SeedRiskProfileAsync(accountId, governor: 1_000m, dailyProfitTarget: 500m, stopForDayAtProfitTarget: false);
        await SeedClosedTradeAsync(accountId, realizedPnL: 600m); // past target, but stand-down is OFF
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

        await Service(suggestionOptions: ThrottleOptions()).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        (await Context().Suggestions.CountAsync()).Should().Be(1);
    }

    // (g) THROTTLED + WINDOW CAP REACHED -> REVIEW, THEN REFUSE: in-band headroom (0.05 of a 0.5 threshold) throttles to
    // a cap of 1; one suggestion already exists this window. Unlike a suppression, the reviewer IS still called (the
    // throttle only caps ISSUANCE here, not whether review runs) -- but the proposal is not staged, and the incumbent is
    // left standing (the refusal returns before the supersede). So the window count stays at the one seeded row.
    [Fact]
    public async Task ScanAsync_ShouldReviewButNotStage_WhenThrottledAndTheWindowCapIsReached()
    {
        Guid accountId = await SeedAccountAsync();
        await SeedRiskProfileAsync(accountId, governor: 1_000m);
        await SeedClosedTradeAsync(accountId, realizedPnL: -950m); // headroom 0.05 -> Throttled, cap 1 (FullWindowCap 1)
        await SeedSuggestionAsync(accountId);                      // the window already holds its one admitted suggestion
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "another setup", 80));

        await Service(suggestionOptions: ThrottleOptions()).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        (await Context().Suggestions.CountAsync()).Should().Be(1); // only the seeded incumbent -- the new one was refused
    }

    // (j) THE CAP COUNTS THE SCAN'S OWN ISSUANCE, NOT THE OPERATOR'S REQUESTS (gh#1134). Byte-for-byte test (g)
    // except that the row already in the window was staged by the CHAT tool rather than the scan. Before this filter
    // the count had no producer clause -- it did not need one, because the scan was the only writer of a Suggestion
    // row -- so the operator asking the co-pilot for setups silently spent the scan's daily cap and the scan went
    // quiet for the rest of the trading day, attributable to nothing they could see. The cap governs what the scan
    // issues UNPROMPTED as headroom depletes; an answer explicitly asked for is not that, and R-5 at take time is the
    // enforcing layer for both. The pair (g)/(j) differs ONLY in the producer, so this cannot pass by the cap being
    // broken generally.
    [Fact]
    public async Task ScanAsync_ShouldStillStage_WhenTheWindowHoldsOnlyAChatProposal()
    {
        Guid accountId = await SeedAccountAsync();
        await SeedRiskProfileAsync(accountId, governor: 1_000m);
        await SeedClosedTradeAsync(accountId, realizedPnL: -950m); // headroom 0.05 -> Throttled, cap 1, exactly as (g)
        await SeedSuggestionAsync(accountId, origin: SuggestionOrigin.Chat); // the ONLY difference from (g)
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "another setup", 80));

        await Service(suggestionOptions: ThrottleOptions()).ScanAsync(Now, CancellationToken.None);

        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.CountAsync()).Should().Be(
            2, "a chat proposal must not consume the scan's per-account daily window — (g) proves the cap still binds");
        (await reload.Suggestions.CountAsync(candidate => candidate.Origin == SuggestionOrigin.Scan)).Should().Be(
            1, "and the row the scan added is the scan's own, stamped as such");
    }

    // (h) THE R-4 INVARIANT -- CONFIDENCE CANNOT LIFT THE CAP: identical to (g) but the proposal comes in at MAXIMUM
    // confidence. The per-window cap is a function of HEADROOM alone; a model-authored 100 can drop a candidate below a
    // floor but can never inflate its way past the deterministic cap. Still refused.
    [Fact]
    public async Task ScanAsync_ShouldRefuseTheSuggestion_EvenAtMaximumConfidence_WhenTheWindowCapIsReached()
    {
        Guid accountId = await SeedAccountAsync();
        await SeedRiskProfileAsync(accountId, governor: 1_000m);
        await SeedClosedTradeAsync(accountId, realizedPnL: -950m);
        await SeedSuggestionAsync(accountId);
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "maximum conviction", 100));

        await Service(suggestionOptions: ThrottleOptions()).ScanAsync(Now, CancellationToken.None);

        (await Context().Suggestions.CountAsync()).Should().Be(1); // the model cannot buy its way past the headroom cap
    }

    // (i) THROTTLED + UNDER CAP -> THE CONVICTION FLOOR FILTERS: in-band headroom (cap 1, floor 70) with an EMPTY window.
    // A candidate at/above the floor is staged; one below it is refused. Confidence is a one-way filter -- it can drop a
    // candidate, never lift the cap (that is (h)). Asserts the resulting issued count both ways.
    [Theory]
    [InlineData(80, 1)] // >= floor 70 and under the cap of 1 -> admitted
    [InlineData(50, 0)] // <  floor 70 -> refused, even under the cap
    public async Task ScanAsync_ShouldApplyTheConvictionFloor_WhenThrottledAndUnderCap(int confidence, int expectedIssued)
    {
        Guid accountId = await SeedAccountAsync();
        await SeedRiskProfileAsync(accountId, governor: 1_000m);
        await SeedClosedTradeAsync(accountId, realizedPnL: -950m); // headroom 0.05 -> Throttled, cap 1, floor 70
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "in-band", confidence));

        await Service(suggestionOptions: ThrottleOptions()).ScanAsync(Now, CancellationToken.None);

        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        (await Context().Suggestions.CountAsync()).Should().Be(expectedIssued);
    }

    // (j) THE WITHIN-PASS TALLY: the throttled cap must count suggestions STAGED earlier in the SAME pass, not only
    // committed rows — the scan commits once at the end of the per-owner loop (gh#455), so a committed-only count would
    // let several same-account fires in one pass each read a stale 0 and all slip under the cap. Cap 1, an EMPTY
    // committed window, TWO agent-review triggers on one account fire this pass: the first stages; the second counts the
    // first's tracked-but-uncommitted Added row and is refused. Exactly one issues (a committed-only count would give 2).
    [Fact]
    public async Task ScanAsync_ShouldCountSuggestionsStagedEarlierThisPass_WhenThrottled()
    {
        Guid accountId = await SeedAccountAsync();
        await SeedRiskProfileAsync(accountId, governor: 1_000m);
        await SeedClosedTradeAsync(accountId, realizedPnL: -950m); // headroom 0.05 -> Throttled, cap 1
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "in-band", 80)); // >= floor, for both fires

        await Service(suggestionOptions: ThrottleOptions()).ScanAsync(Now, CancellationToken.None);

        // Both fires REVIEW (Throttled runs the reviewer); the cap of 1 admits only the first. Had the count read only
        // committed rows, both would have seen 0 and staged -> 2. The within-pass tally is what holds the cap here.
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedTwiceExactly();
        (await Context().Suggestions.CountAsync()).Should().Be(1);
    }

    // (k) FAIL-OPEN on a throttle-read fault: a faulting throttle-state read must let the pass PROPOSE (the reviewer
    // still wakes, the suggestion stages) and never abort the pass — the same posture as the AI-spend spend read, so a
    // DB blip cannot pause the co-pilot or take the co-located mechanical route down with it. The seeded loss WOULD
    // suppress had the read succeeded, so a fail-CLOSED bug would leave zero suggestions — this pins fail-open, not that.
    [Fact]
    public async Task ScanAsync_ShouldFailOpenAndStillPropose_WhenTheThrottleReadFaults()
    {
        Guid accountId = await SeedAccountAsync();
        await SeedRiskProfileAsync(accountId, governor: 1_000m);
        await SeedClosedTradeAsync(accountId, realizedPnL: -1_000m); // would SUPPRESS (governor reached) if the read succeeded
        Guid id = await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "oversold", 72));

        TriggerEvaluationService service = new ThrowingThrottleReadService(
            Context(), Options, _indicators, _notifications, _reviewer, _enrichment, _ledger, _llmMetrics, _deadlines,
            Microsoft.Extensions.Options.Options.Create(ThrottleOptions()), new AiSpendGovernor(),
            Microsoft.Extensions.Options.Options.Create(new GovernorOptions()),
            _suggestionNotifier, new SuggestionThrottle(), _levels, _specs,
            Microsoft.Extensions.Options.Options.Create(_inertConfluence), NullLogger<TriggerEvaluationService>.Instance);

        Func<Task> act = () => service.ScanAsync(Now, CancellationToken.None);

        await act.Should().NotThrowAsync();
        // Fail-OPEN, not fail-closed: the reviewer runs and the suggestion stages, exactly as if un-throttled — even
        // though the seeded loss would otherwise have suppressed. DO NOT change the production path to fail-closed.
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._)).MustHaveHappenedOnceExactly();
        await using TradingCopilotDbContext reload = Context();
        (await reload.Suggestions.CountAsync()).Should().Be(1);
        (await reload.Triggers.SingleAsync(t => t.Id == id)).ArmState.Should().Be(TriggerArmState.Fired);
    }

    // (l) R-14 MODE FILTER at the throttle call site (gh#746 review). The account is Live with trades on BOTH sides
    // of a mode change: a large PRACTICE loss (the old mode) that would suppress if it were counted, and a small LIVE
    // loss (the current mode) that leaves ample headroom. The throttle reads the account's CURRENT mode, so it counts
    // only the Live trade -- the pass stays un-throttled and the suggestion issues. A read that summed both (the
    // pre-gh#746 blend, or a swapped mode argument) would see the governor reached and SUPPRESS. This is the one call
    // site #746's criterion left unproven; without the filter, no test here goes red.
    [Fact]
    public async Task ScanAsync_ShouldThrottleOnlyTheCurrentModesRealizedPnL_WhenTheAccountChangedModes()
    {
        Guid accountId = await SeedAccountAsync(mode: TradingMode.Live);
        await SeedRiskProfileAsync(accountId, governor: 1_000m);
        await SeedClosedTradeAsync(accountId, realizedPnL: -950m, mode: TradingMode.Practice); // old mode -- would suppress
        await SeedClosedTradeAsync(accountId, realizedPnL: -100m, mode: TradingMode.Live);      // current mode -- ample headroom
        await AddTriggerAsync(route: TriggerRoute.AgentReview, accountId: accountId, size: 1, armState: TriggerArmState.Armed);
        IndicatorReturns(25m);
        ReviewerReturns(new ReviewOutcome.Suggest(OrderSide.Buy, 100m, 99m, 103m, "current-mode day", 80));

        await Service(suggestionOptions: ThrottleOptions()).ScanAsync(Now, CancellationToken.None);

        // Live-only dayLoss is 100 of a 1000 governor -> Full -> un-throttled, so the suggestion issues. Summing the
        // Practice loss too would read dayLoss 1050 >= governor -> SUPPRESSED, and the reviewer would never run.
        A.CallTo(() => _reviewer.ReviewAsync(A<TriggerReviewContext>._, A<CancellationToken>._, A<bool>._))
            .MustHaveHappenedOnceExactly();
        (await Context().Suggestions.CountAsync()).Should().Be(1);
    }

    // (m) THE THROTTLE'S Undeclared BRANCH (gh#746 review). An account regressed to Undeclared with a real prior-mode
    // (Live) loss on the books. The throttle folds Undeclared into the vanished-account inert-0 path -- an Undeclared
    // account trades nowhere, so its throttle is moot. This asserts the read returns 0m directly, because end-to-end
    // the Undeclared staging gate would mask a wrong throttle read (the existing Undeclared scan test seeds no trade
    // and cannot tell the two apart). It is a genuine prove-red now that the reader REFUSES Undeclared: revert the
    // `or TradingMode.Undeclared` and the read throws instead of returning 0m -- so this can fail on the exact defect.
    [Fact]
    public async Task ReadThrottleState_ShouldReadInertZero_WhenTheAccountIsUndeclared()
    {
        Guid accountId = await SeedAccountAsync(mode: TradingMode.Undeclared);
        await SeedRiskProfileAsync(accountId, governor: 1_000m);
        await SeedClosedTradeAsync(accountId, realizedPnL: -800m, mode: TradingMode.Live); // a real Live loss, still on the books

        ThrottleStateProbe probe = new(
            Context(), Options, _indicators, _notifications, _reviewer, _enrichment, _ledger, _llmMetrics, _deadlines,
            Microsoft.Extensions.Options.Options.Create(ThrottleOptions()), new AiSpendGovernor(),
            Microsoft.Extensions.Options.Options.Create(new GovernorOptions()),
            _suggestionNotifier, new SuggestionThrottle(), _levels, _specs,
            Microsoft.Extensions.Options.Options.Create(_inertConfluence), NullLogger<TriggerEvaluationService>.Instance);

        (RiskProfileRecord? Profile, decimal DayRealized) state =
            await probe.ProbeThrottleStateAsync(Context(), accountId, Now, CancellationToken.None);

        state.Profile.Should().NotBeNull("the account has a declared profile");
        state.DayRealized.Should().Be(
            0m,
            "an Undeclared account is inert -- its prior-mode loss is not projected into a current-mode governor, and "
            + "the reader is never called with Undeclared (which now throws), so this is 0 by intent, not a filter miss");
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
            ISessionDeadlineSource deadlines,
            IOptions<SuggestionOptions> suggestionOptions,
            IAiSpendGovernor governor,
            IOptions<GovernorOptions> governorOptions,
            ISuggestionRealtimeNotifier suggestionNotifier,
            ISuggestionThrottle throttle,
            IPriceLevelSource levels,
            IInstrumentSpecSource specs,
            IOptions<ConfluenceOptions> confluenceOptions,
            ILogger<TriggerEvaluationService> logger)
            : base(discovery, options, indicators, notifications, reviewer, enrichmentSource, ledger, metrics, deadlines, suggestionOptions, governor, governorOptions, suggestionNotifier, throttle, levels, specs, confluenceOptions, logger)
        {
        }

        protected override Task<decimal> ReadWindowSpendAsync(DateTimeOffset windowStart, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated spend-read fault");
    }

    /// <summary>
    /// The scan with its R-4 throttle-state read forced to FAULT (gh#551) — proves the throttle is FAIL-OPEN: a
    /// throttle-read blip lets the pass propose un-throttled (the reviewer still wakes), never aborting the owner's pass.
    /// Forwards all fifteen ctor args. DO NOT "fix" the production counterpart to fail-closed (suppress) — a transient
    /// blip would then pause the co-pilot and emit spurious "Suggestions paused" advisories.
    /// </summary>
    private sealed class ThrowingThrottleReadService : TriggerEvaluationService
    {
        public ThrowingThrottleReadService(
            TradingCopilotDbContext discovery,
            DbContextOptions<TradingCopilotDbContext> options,
            IIndicatorSource indicators,
            INotificationChannel notifications,
            ITriggerReviewer reviewer,
            IReviewEnrichmentSource enrichmentSource,
            IAiUsageLedger ledger,
            ILlmMetrics metrics,
            ISessionDeadlineSource deadlines,
            IOptions<SuggestionOptions> suggestionOptions,
            IAiSpendGovernor governor,
            IOptions<GovernorOptions> governorOptions,
            ISuggestionRealtimeNotifier suggestionNotifier,
            ISuggestionThrottle throttle,
            IPriceLevelSource levels,
            IInstrumentSpecSource specs,
            IOptions<ConfluenceOptions> confluenceOptions,
            ILogger<TriggerEvaluationService> logger)
            : base(discovery, options, indicators, notifications, reviewer, enrichmentSource, ledger, metrics, deadlines, suggestionOptions, governor, governorOptions, suggestionNotifier, throttle, levels, specs, confluenceOptions, logger)
        {
        }

        protected override Task<(RiskProfileRecord? Profile, decimal DayRealized)> ReadThrottleStateAsync(
            TradingCopilotDbContext database, Guid accountId, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated throttle-state read fault");
    }

    // Exposes the real (base) throttle-state read so a test can inspect its (Profile, DayRealized) directly -- the
    // only way to prove the Undeclared branch, since end-to-end the Undeclared staging gate would mask it (gh#746).
    private sealed class ThrottleStateProbe : TriggerEvaluationService
    {
        public ThrottleStateProbe(
            TradingCopilotDbContext discovery,
            DbContextOptions<TradingCopilotDbContext> options,
            IIndicatorSource indicators,
            INotificationChannel notifications,
            ITriggerReviewer reviewer,
            IReviewEnrichmentSource enrichmentSource,
            IAiUsageLedger ledger,
            ILlmMetrics metrics,
            ISessionDeadlineSource deadlines,
            IOptions<SuggestionOptions> suggestionOptions,
            IAiSpendGovernor governor,
            IOptions<GovernorOptions> governorOptions,
            ISuggestionRealtimeNotifier suggestionNotifier,
            ISuggestionThrottle throttle,
            IPriceLevelSource levels,
            IInstrumentSpecSource specs,
            IOptions<ConfluenceOptions> confluenceOptions,
            ILogger<TriggerEvaluationService> logger)
            : base(discovery, options, indicators, notifications, reviewer, enrichmentSource, ledger, metrics, deadlines, suggestionOptions, governor, governorOptions, suggestionNotifier, throttle, levels, specs, confluenceOptions, logger)
        {
        }

        public Task<(RiskProfileRecord? Profile, decimal DayRealized)> ProbeThrottleStateAsync(
            TradingCopilotDbContext database, Guid accountId, DateTimeOffset now, CancellationToken cancellationToken) =>
            ReadThrottleStateAsync(database, accountId, now, cancellationToken);
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
