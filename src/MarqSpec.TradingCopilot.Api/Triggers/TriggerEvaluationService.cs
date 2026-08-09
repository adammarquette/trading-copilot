using System.Diagnostics;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// The deterministic trigger scan's core (gh#385, R-4 / R-7, ADR-0008): each pass evaluates every enabled
/// <b>mechanical</b> trigger against its resolved indicator value and fires the crossing edges as alerts — no LLM in
/// the loop. The transition logic is the pure <see cref="TriggerDebounce"/>; this service resolves the inputs,
/// sends the alert, and persists the resulting arm state.
/// </summary>
/// <remarks>
/// <para>
/// Background plumbing with no request user: it <b>discovers</b> owners of enabled mechanical triggers with
/// <c>IgnoreQueryFilters</c> (the firing-watcher pattern), then does the work for each owner in a DbContext
/// <b>scoped to that owner</b>, so every read and write stays R-20-correct. Indicators are read through the
/// <b>global</b> <see cref="IIndicatorSource"/> (derived market data sits on the shared side of R-20's line), and
/// cached per <c>(symbol, indicator, period, resolution)</c> for the pass, since the read is a pure function of the
/// passed-in <c>now</c>.
/// </para>
/// <para>
/// Two orderings are the point of the design. <b>Send-before-commit:</b> the alert is sent <i>before</i> the
/// <c>SaveChanges</c> that commits the fired state, so a failed send never leaves a trigger marked fired-but-unsent
/// — an alert layer fails toward notifying, never toward silence. <b>Fail-closed null:</b> a null indicator reads
/// <see cref="ConditionSatisfaction.Unmeasurable"/>, which <see cref="TriggerDebounce"/> holds — never a fire, never
/// a re-arm — so a data gap cannot collapse a fired trigger and spuriously re-fire it.
/// </para>
/// <para>
/// The AI-spend governor (gh#448, ADR-0008) gates the agent-review route only: once per pass the scan reads the
/// deployment-wide daily AI spend (the <c>AIUsage</c> ledger floor) and, before waking the reviewer on a fire, a
/// pure <see cref="IAiSpendGovernor"/> blocks the LLM call when the operator's budget is spent. The class is
/// <b>unsealed</b> (not <c>sealed</c>) so a test can override <see cref="ReadWindowSpendAsync"/> to force the
/// fail-open read fault — the same reason <c>AiUsageLedger</c> exposes a <c>protected virtual</c> write.
/// </para>
/// </remarks>
public class TriggerEvaluationService
{
    private readonly TradingCopilotDbContext _discovery;
    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly IIndicatorSource _indicators;
    private readonly INotificationChannel _notifications;
    private readonly ITriggerReviewer _reviewer;
    private readonly IReviewEnrichmentSource _enrichmentSource;
    private readonly IAiUsageLedger _ledger;
    private readonly ILlmMetrics _llmMetrics;
    private readonly ISessionDeadlineSource _deadlines;
    private readonly TimeSpan _suggestionValidity;
    private readonly IAiSpendGovernor _governor;
    private readonly AiSpendBudget? _budget;
    private readonly ISuggestionThrottle _throttle;
    private readonly bool _throttleEnabled;
    private readonly decimal _throttleThreshold;
    private readonly int _throttleFullWindowCap;
    private readonly int _throttleConvictionFloor;
    private readonly ISuggestionRealtimeNotifier _suggestionNotifier;
    private readonly ILogger<TriggerEvaluationService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used to discover owners and to read the platform-wide spend total.</param>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context for the work.</param>
    /// <param name="indicators">The global read seam for pre-computed indicator values (R-22).</param>
    /// <param name="notifications">The alerting seam (ADR-0019); both routes send through it.</param>
    /// <param name="reviewer">
    /// The agent-review judgment seam (ADR-0008): the agent-review route wakes it once per fire. It only <b>proposes</b>
    /// — it is not, and must not become, a path to execution (enforcement lives below the model).
    /// </param>
    /// <param name="enrichmentSource">
    /// The deep-tier enrichment seam (gh#476): the scan assembles the numeric market context here and attaches it to the
    /// review context before waking the reviewer. Kept on the scan (not the reviewer) so the reviewer's dependency set
    /// stays pure of data-access types; fail-open — a read fault leaves the context un-enriched, never blocks a fire.
    /// </param>
    /// <param name="ledger">
    /// The AIUsage spend ledger (gh#431): a required, fail-open dependency the scan stamps the owner onto — it records
    /// the reviewer's LLM-call cost and can never fail or roll back a fire.
    /// </param>
    /// <param name="metrics">
    /// The LLM-spend meter (gh#477): a required, export-only observability seam fed the same per-call <c>AiCallCost</c>
    /// as the ledger, so Grafana sees true LLM spend. Not enforcement — the governor still reads the ledger floor.
    /// </param>
    /// <param name="deadlines">
    /// The session-deadline read seam (gh#544): supplies the market's Central wall-clock deadline so an issued
    /// suggestion's validity window can be clamped to it. Deliberately <b>not</b> the flatten options — the
    /// agent-review path must be able to respect the deadline without depending on the machinery that acts on it,
    /// which the gh#402 constructor-graph guard enforces.
    /// </param>
    /// <param name="suggestionOptions">The suggestion config; supplies the default validity window (gh#544).</param>
    /// <param name="governor">
    /// The platform-level AI-spend gate (gh#448): a pure, deterministic budget check consulted before each agent-review
    /// LLM call. It gates <b>whether</b> a call is made (cost), never what it proposes (enforcement lives below the model).
    /// </param>
    /// <param name="governorOptions">The governor's budget config; absent (null budget) leaves the governor inert.</param>
    /// <param name="suggestionNotifier">
    /// The realtime push seam (gh#684): a read-side, best-effort notifier called AFTER a suggestion write commits, so
    /// an issued / superseded row reaches the owning operator's live surfaces without a poll. Presentation-only — its
    /// failure never affects the write (enforcement lives below the model; this is below the write).
    /// </param>
    /// <param name="throttle">
    /// The R-4 suggestion throttle (gh#551): a pure, deterministic policy consulted BEFORE the reviewer wakes. Fed the
    /// account's headroom as data — never a gate/venue dependency (the gh#402 gate-below-model discipline) — it
    /// suppresses or caps issuance as daily-drawdown headroom depletes. Inert unless <see cref="SuggestionOptions.ThrottleEnabled"/>.
    /// </param>
    /// <param name="logger">The logger.</param>
    public TriggerEvaluationService(
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
        ILogger<TriggerEvaluationService> logger)
    {
        ArgumentNullException.ThrowIfNull(governorOptions);
        ArgumentNullException.ThrowIfNull(suggestionOptions);

        _discovery = discovery;
        _options = options;
        _indicators = indicators;
        _notifications = notifications;
        _reviewer = reviewer;
        _enrichmentSource = enrichmentSource;
        _ledger = ledger;
        _llmMetrics = metrics;
        _deadlines = deadlines;
        _suggestionValidity = suggestionOptions.Value.Validity;
        _governor = governor;
        _budget = governorOptions.Value.ToBudget(); // null == inert (no cap configured); computed once per pass
        _suggestionNotifier = suggestionNotifier;
        _throttle = throttle;
        _throttleEnabled = suggestionOptions.Value.ThrottleEnabled;
        _throttleThreshold = suggestionOptions.Value.ThrottleThresholdFraction;
        _throttleFullWindowCap = suggestionOptions.Value.ThrottleFullWindowCap;
        _throttleConvictionFloor = suggestionOptions.Value.ThrottleConvictionFloor;
        _logger = logger;
    }

    /// <summary>Evaluates every enabled mechanical trigger and fires the crossing edges.</summary>
    /// <param name="now">The moment to evaluate as of — supplied by the caller; the service never reads a clock.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many triggers fired this pass.</returns>
    public async Task<int> ScanAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Discover owners with CONFIRMED, enabled mechanical OR agent-review triggers -- background, so the R-20 filter
        // is bypassed here. An unconfirmed trigger is inert regardless of Enabled (gh#470): the operator has never
        // accepted it into the firing set, so it is neither discovered nor evaluated.
        List<Guid> owners = await _discovery.Triggers
            .IgnoreQueryFilters()
            .Where(trigger => trigger.Confirmation == TriggerConfirmation.Confirmed
                && trigger.Enabled
                && (trigger.Route == TriggerRoute.Mechanical || trigger.Route == TriggerRoute.AgentReview))
            .Select(trigger => trigger.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (owners.Count == 0)
        {
            return 0;
        }

        // AI-SPEND GOVERNOR (gh#448): read the deployment-wide daily spend ONCE per pass and seed a mutable tally each
        // fire accrues into, so later fires this pass evaluate against rising spend (the per-fire risk-gate mirror).
        // Null == the governor is inert (no budget) OR the read faulted (fail-open) -- either way the pass runs un-gated.
        GovernorPass? governorPass = await BuildGovernorPassAsync(now, cancellationToken);

        int fires = 0;
        foreach (Guid owner in owners)
        {
            try
            {
                fires += await ProcessOwnerAsync(owner, now, governorPass, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // One owner's fault (a DB blip, a wedged read/send) must not starve the others this pass. Its
                // scoped context is discarded with its uncommitted changes; the owner's triggers recompute next
                // pass (idempotent), so a fault costs at most a one-interval delay.
                _logger.LogError(error, "Trigger scan failed for owner {Owner}; the next pass retries.", owner);
            }
        }

        // THRESHOLD PRE-ALERT (gh#448): after the owner loop, against the freshest accrued tally, a heads-up (Notify,
        // not Page) when spend nears the budget -- the "make the constraint visible before it binds" posture. The
        // Central-date-scoped dedup key emits it at most once per trading day. Best-effort: never throws into the pass.
        await MaybeAlertThresholdAsync(governorPass, now, cancellationToken);

        if (fires > 0)
        {
            _logger.LogInformation("Trigger scan fired {Count} trigger(s).", fires);
        }

        return fires;
    }

    /// <summary>
    /// Reads the platform-wide AI spend since <paramref name="windowStart"/> — the deployment's floor for the governor
    /// (gh#448). Crosses the R-20 default-deny filter with <c>IgnoreQueryFilters</c> (ADR-0008: one shared account
    /// funds every user), so it sums <b>every</b> owner's rows and the <see cref="SystemOwner"/> embed-sentinel rows.
    /// The nullable projection is mandatory — <c>SUM</c> over an empty window returns <c>NULL</c>, which the
    /// non-nullable overload would throw on. Virtual so a test can force a read fault to prove the fail-open posture.
    /// </summary>
    /// <param name="windowStart">The inclusive start of the spend window (UTC).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The summed estimated cost in USD, or zero for an empty window.</returns>
    protected virtual async Task<decimal> ReadWindowSpendAsync(DateTimeOffset windowStart, CancellationToken cancellationToken) =>
        await _discovery.AiUsage
            .IgnoreQueryFilters()
            .Where(record => record.OccurredAt >= windowStart)
            .Select(record => (decimal?)record.EstimatedCostUsd)
            .SumAsync(cancellationToken) ?? 0m;

    /// <summary>Builds the per-pass governor tally, or <see langword="null"/> when inert or the spend read faulted.</summary>
    private async Task<GovernorPass?> BuildGovernorPassAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_budget is null)
        {
            return null; // no cap configured -- the governor is inert, the pass runs exactly as before gh#448
        }

        try
        {
            decimal spent = await ReadWindowSpendAsync(CentralDayStartUtc(now), cancellationToken);
            return new GovernorPass(_budget, spent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // FAIL-OPEN -- the deliberate INVERSE of the fail-closed risk gate. This gate guards a soft-dollar BUDGET,
            // not capital-at-risk: a spend-read blip must NOT pause agent review, and must NOT abort the pass (which
            // would also kill the co-located MECHANICAL alert route). Log loudly (spend-blindness is a real fault),
            // then run this pass un-gated; the next pass re-reads. DO NOT "fix" this to fail-closed by analogy to
            // RiskGate -- that would silently pause all agent review on any DB hiccup, to protect pennies.
            _logger.LogError(error, "AI-spend governor could not read spend this pass; agent review runs un-gated.");
            return null;
        }
    }

    /// <summary>Fires the once-per-day threshold heads-up if the accrued spend has reached the alert fraction.</summary>
    private async Task MaybeAlertThresholdAsync(GovernorPass? governorPass, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (governorPass is null)
        {
            return;
        }

        AiSpendDecision decision = _governor.Evaluate(governorPass.Budget, governorPass.SpentUsd);
        if (!decision.ThresholdReached)
        {
            return;
        }

        try
        {
            int percent = (int)(decision.ConsumedFraction * 100m);
            // ThresholdReached is forced true on a Block, so the daily heads-up must distinguish "nearing" from
            // "already spent" -- otherwise an over-budget day (e.g. embed spend jumping past 100% in one interval, or
            // a mid-day restart clearing the once-per-day dedup) would read "nearing ... 1000%", self-contradictory.
            // The per-arming-edge BudgetExhausted advisory covers each paused setup; this is the deployment-wide note.
            (string title, string body) = decision.IsBlocked
                ? ("AI daily budget reached",
                    FormattableString.Invariant(
                        $"AI spend is {percent}% of the {decision.BudgetUsd} USD daily budget; the daily cap is reached until the trading day rolls over."))
                : ("AI spend nearing the daily budget",
                    FormattableString.Invariant($"AI spend is {percent}% of the {decision.BudgetUsd} USD daily budget."));
            await _notifications.SendAsync(
                new Notification(NotificationSeverity.Notify, title, body, ThresholdDedupKey(now)),
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // INotificationChannel's contract is never-throws; a buggy channel must not take down the scan for a
            // best-effort heads-up.
            _logger.LogError(error, "AI-spend threshold alert failed to send; spend tracking is unaffected.");
        }
    }

    /// <summary>Evaluates one owner's enabled mechanical + agent-review triggers in a per-owner (R-20-scoped) context.</summary>
    private async Task<int> ProcessOwnerAsync(
        Guid owner, DateTimeOffset now, GovernorPass? governorPass, CancellationToken cancellationToken)
    {
        // Per-owner context: the R-20 filter applies, so every trigger read and every firing / suggestion written is
        // this owner's alone -- one owner's SaveChanges can never persist another's rows.
        await using TradingCopilotDbContext database = new(_options, new OwnerUser(owner));

        List<TriggerRecord> triggers = await database.Triggers
            .Where(trigger => trigger.Confirmation == TriggerConfirmation.Confirmed
                && trigger.Enabled
                && (trigger.Route == TriggerRoute.Mechanical || trigger.Route == TriggerRoute.AgentReview))
            .ToListAsync(cancellationToken);

        // Cache the global indicator read per series for the pass: many triggers can share one series, and the
        // read is a pure function of `now`.
        Dictionary<(string Symbol, string Indicator, int Period, int Resolution), decimal?> cache = new();
        int fires = 0;
        bool changed = false;

        // The AGENT-REVIEW route queues its advisories to flush AFTER the commit (commit-then-notify): the Suggestion
        // is the durable artifact, the notify is best-effort and must never re-arm. The MECHANICAL route does not use
        // this -- it sends BEFORE the commit and re-arms on non-delivery. The two orderings are kept apart on purpose.
        List<Notification> advisories = [];

        // Alongside the advisories (gh#684): the per-owner realtime pushes for suggestions issued / superseded THIS
        // pass, flushed AFTER the same commit so a hub fault never affects the write, and never pushing an uncommitted row.
        List<RealtimeSuggestion> suggestionPushes = [];

        foreach (TriggerRecord trigger in triggers)
        {
            IndicatorThresholdCondition condition = trigger.ToCondition();

            (string, string, int, int) key = (trigger.Symbol, trigger.Indicator, trigger.Period, trigger.ResolutionMinutes);
            if (!cache.TryGetValue(key, out decimal? value))
            {
                value = await _indicators.GetValueAsync(
                    condition.Instrument, trigger.Indicator, trigger.Period, trigger.ResolutionMinutes, now, cancellationToken);
                cache[key] = value;
            }

            ConditionSatisfaction satisfaction = condition.Evaluate(value);
            TriggerDecision decision = TriggerDebounce.Decide(trigger.ArmState, satisfaction);

            // Every pass records what it measured and the next arm state, fire or not.
            trigger.LastEvaluatedValue = value;
            trigger.ArmState = decision.NextState;
            changed = true;

            // gh#469: holding on an unmeasurable reading was always right, but silent. A trigger whose dependency
            // stopped being produced would simply never fire, and nothing distinguished that from a condition that
            // never occurred. Duration is what separates a late bar from a broken trigger, so the outage start is
            // persisted and reported once it outlasts the threshold -- never every pass, which would be a log line
            // per trigger per poll.
            TriggerStaleness staleness = TriggerStaleness.Track(trigger.UnmeasurableSince, trigger.StalenessReportedAt, satisfaction, now);
            trigger.UnmeasurableSince = staleness.UnmeasurableSince;
            trigger.StalenessReportedAt = staleness.ReportedAt;

            if (staleness.ShouldReport)
            {
                _logger.LogWarning(
                    "Trigger {TriggerId} has been unevaluable since {Since}: no {Indicator}({Period}) at "
                    + "{Resolution}m for {Symbol}. It cannot fire until that indicator is produced again — check "
                    + "Ingestion:Symbols, Backfill:ResolutionMinutes and the configured indicator set. The trigger "
                    + "is NOT disabled.",
                    trigger.Id, staleness.UnmeasurableSince, trigger.Indicator, trigger.Period,
                    trigger.ResolutionMinutes, trigger.Symbol);
            }

            if (decision.ShouldFire && trigger.Route == TriggerRoute.Mechanical)
            {
                // MECHANICAL: send-before-commit, UNCHANGED -- the governor gates the LLM route only, never a
                // deterministic alert. The `!` is sound -- a fire needs a measured, satisfied reading, so non-null here.
                if (await FireMechanicalAsync(database, trigger, owner, value!.Value, now, cancellationToken))
                {
                    fires++;
                }
                else
                {
                    // SEND-BEFORE-COMMIT non-delivery: leave the trigger ARMED so the next pass re-attempts the arming
                    // edge, rather than committing a fired-but-unsent alert that would then debounce and be lost.
                    trigger.ArmState = TriggerArmState.Armed;
                    _logger.LogWarning(
                        "Trigger {Id} alert was not accepted for delivery; it stays armed and re-attempts next scan.",
                        trigger.Id);
                }
            }
            else if (decision.ShouldFire && trigger.Route == TriggerRoute.AgentReview)
            {
                // AGENT-REVIEW: wake the reviewer, stage any suggestion + queue any advisory, and journal the firing.
                // A fire is a fire regardless of the outcome -- suppress advances the arm to Fired too, which is what
                // debounces the review to ONE per arming edge (a persistently-true condition must not re-review every
                // pass). COMMIT-THEN-NOTIFY: the advisory flushes after SaveChanges below.
                await FireAgentReviewAsync(database, trigger, owner, value!.Value, now, advisories, suggestionPushes, governorPass, cancellationToken);
                fires++;
            }
            else if (decision.ReArmed)
            {
                // Close the incident under the CURRENT cycle key, THEN bump the cycle so the next crossing mints a
                // fresh key -- a distinct incident even if this resolve is missed.
                await _notifications.ResolveAsync(DedupKeyFor(trigger), cancellationToken);
                trigger.ArmCycle++;
            }
        }

        if (changed)
        {
            await database.SaveChangesAsync(cancellationToken);
        }

        // REALTIME PUSH (gh#684): the issued / superseded rows are now durable, so push each lifecycle change to the
        // OWNING operator's connections (R-20, Clients.User -- never broadcast). Best-effort and AFTER the commit,
        // exactly like the advisory flush below: a hub fault must never fail or roll back the write. A SaveChanges that
        // THREW skips this entirely (the per-owner guard discards the pass), so only committed rows are ever pushed.
        foreach (RealtimeSuggestion push in suggestionPushes)
        {
            await NotifySafelyAsync(owner, push, cancellationToken);
        }

        // COMMIT-THEN-NOTIFY (agent-review only): the Suggestion + firing + arm advance are now durable, so flush the
        // advisories. A false / failed send only LOGS -- it must not re-arm (re-arming would re-review and persist a
        // DUPLICATE suggestion) and must not block. This is the INVERSE of the mechanical route above.
        foreach (Notification advisory in advisories)
        {
            try
            {
                if (!await _notifications.SendAsync(advisory, cancellationToken))
                {
                    _logger.LogWarning(
                        "Agent-review advisory for incident {DedupKey} was not accepted for delivery; the reviewed "
                        + "setup is persisted regardless.",
                        advisory.DedupKey);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The channel contract says SendAsync never throws; if a buggy one does, the advisory is lost but the
                // suggestion stays durable -- a best-effort notify must never undo a committed artifact.
                _logger.LogError(
                    error,
                    "Agent-review advisory for incident {DedupKey} failed to send; the reviewed setup is persisted regardless.",
                    advisory.DedupKey);
            }
        }

        return fires;
    }

    /// <summary>The mechanical send-before-commit fire: emit the alert, and journal only on accepted delivery.</summary>
    /// <returns><see langword="true"/> when the alert was accepted and the firing journaled; <see langword="false"/> on non-delivery.</returns>
    private async Task<bool> FireMechanicalAsync(
        TradingCopilotDbContext database, TriggerRecord trigger, Guid owner, decimal observed, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string dedupKey = DedupKeyFor(trigger);

        // SEND-BEFORE-COMMIT: emit BEFORE the SaveChanges that commits Fired. The channel returns false -- it never
        // throws, per INotificationChannel's contract -- when it could not accept the alert (a wedged transport
        // filling the bounded queue). A throw here propagates to the per-owner guard, which discards the scoped
        // context with its uncommitted Fired state, so the trigger stays Armed either way.
        bool sent = await _notifications.SendAsync(
            new Notification(trigger.Severity, TitleFor(trigger), BodyFor(trigger, observed), dedupKey),
            cancellationToken);

        if (!sent)
        {
            return false;
        }

        JournalFiring(database, trigger, owner, observed, now, dedupKey, Guid.NewGuid());
        trigger.LastFiredAt = now;
        return true;
    }

    /// <summary>
    /// The agent-review fire: consult the spend governor, wake the reviewer (unless the budget is spent), stage a
    /// suggestion (or an advisory) per the outcome, and journal the firing regardless. NOTHING here reaches execution
    /// — it persists a suggestion and at most queues an advisory (enforcement lives below the model).
    /// </summary>
    // The inert throttle decision: Full, an uncapped window admitting every candidate — used when the throttle is off
    // or the account declares no governor. Admits(...) is always true (cap = int.MaxValue) and IsSuppressed is false,
    // so the scan behaves exactly as before opt-in.
    private static readonly SuggestionThrottleDecision _fullThrottle = new(
        SuggestionThrottleMode.Full, PerWindowCap: int.MaxValue, RequiredConfidence: 0,
        SuggestionThrottleReason.None, "Suggestion throttle inert — proposing normally.");

    /// <summary>
    /// Decides the R-4 throttle for a fire's account (gh#551), fed the account's declared governor and its realized
    /// day P&amp;L as DATA from this owner's own R-20-filtered context — never a gate / venue dependency (gh#402). Inert
    /// (<see cref="_fullThrottle"/>) when the throttle is off, the trigger names no account, or no risk profile exists.
    /// </summary>
    private async Task<SuggestionThrottleDecision> DecideThrottleAsync(
        TradingCopilotDbContext database, TriggerRecord trigger, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_throttleEnabled || trigger.AccountId is not { } accountId)
        {
            return _fullThrottle;
        }

        try
        {
            (RiskProfileRecord? profile, decimal dayRealized) =
                await ReadThrottleStateAsync(database, accountId, now, cancellationToken);

            // No declared profile => no governor to throttle against: propose (inert), like an account that set no limit.
            if (profile is null)
            {
                return _fullThrottle;
            }

            decimal dayLoss = dayRealized < 0m ? -dayRealized : 0m; // the non-negative loss the headroom projection consumes
            decimal dayProfit = dayRealized > 0m ? dayRealized : 0m;

            decimal headroomFraction = DailyHeadroom
                .Remaining(profile.DailyLossLimit, profile.DailyDrawdownGovernor, dayLoss)
                .GovernorFractionRemaining(profile.DailyDrawdownGovernor);

            // The RAW target-reached, NOT pre-ANDed with StopForDayAtProfitTarget: the policy applies that flag itself, so
            // feeding the pre-ANDed bool would double-apply it — a green day past target with stand-down OFF must stay Full.
            bool dailyTargetReached = profile.DailyProfitTarget is { } target && dayProfit >= target;

            SuggestionThrottlePolicy policy = SuggestionThrottlePolicy.Declare(
                _throttleThreshold, _throttleFullWindowCap, _throttleConvictionFloor, profile.StopForDayAtProfitTarget);

            return _throttle.Decide(policy, new SuggestionThrottleContext(headroomFraction, dailyTargetReached));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // FAIL-OPEN, mirroring the AI-spend governor's spend read (BuildGovernorPassAsync): a throttle-read fault —
            // a DB blip, or (defensively) a policy value that slipped past startup validation — must NOT abort this
            // owner's whole scan pass and take the co-located mechanical route down with it, which would re-send an
            // already-sent mechanical alert next pass. The throttle is ADVISORY, ahead of the execution gate that
            // enforces real risk below the model, and a suggestion carries no risk — so proposing un-throttled on a read
            // fault is the safe direction, exactly as the governor proposes on a spend-read fault. DO NOT "fix" this to
            // fail-closed (suppress): that would pause the co-pilot and emit spurious "Suggestions paused" advisories on
            // a transient blip. The next pass re-reads fresh.
            _logger.LogError(
                error, "R-4 throttle state read faulted for account {Account}; proposing un-throttled this pass.", accountId);
            return _fullThrottle;
        }
    }

    /// <summary>
    /// Reads the throttle's inputs for an account (gh#551): its <see cref="RiskProfileRecord"/> — <see langword="null"/>
    /// when none is declared — and the day's <b>signed</b> realized P&amp;L over the Central trading day. A
    /// <see langword="protected"/> <see langword="virtual"/> seam so a test can force the read to fault and prove
    /// <see cref="DecideThrottleAsync"/>'s fail-open posture, mirroring <see cref="ReadWindowSpendAsync"/>. The P&amp;L
    /// read is skipped when no profile exists (there is nothing to throttle against).
    /// </summary>
    protected virtual async Task<(RiskProfileRecord? Profile, decimal DayRealized)> ReadThrottleStateAsync(
        TradingCopilotDbContext database, Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RiskProfileRecord? profile = await database.RiskProfiles
            .FirstOrDefaultAsync(candidate => candidate.AccountId == accountId, cancellationToken);
        decimal dayRealized = profile is null
            ? 0m
            : await database.TodayRealizedPnLForAccountAsync(accountId, now, cancellationToken);
        return (profile, dayRealized);
    }

    // Maps the throttle's suppress reason to the review-outcome reason the scan's advisory switch renders. IsSuppressed
    // implies one of these two, so the fall-through to GovernorReached is never reached for a non-suppressed decision.
    private static SuppressReason ThrottleSuppress(SuggestionThrottleReason reason) => reason switch
    {
        SuggestionThrottleReason.DailyTargetStandDown => SuppressReason.DailyTargetStandDown,
        _ => SuppressReason.GovernorReached,
    };

    private async Task FireAgentReviewAsync(
        TradingCopilotDbContext database,
        TriggerRecord trigger,
        Guid owner,
        decimal observed,
        DateTimeOffset now,
        List<Notification> advisories,
        List<RealtimeSuggestion> suggestionPushes,
        GovernorPass? governorPass,
        CancellationToken cancellationToken)
    {
        string dedupKey = DedupKeyFor(trigger);

        // Minted up front (gh#542) so a staged suggestion can cite the firing it came from: the firing row is
        // journaled at the end of this method, but both land in the same SaveChanges, so the id is a valid link.
        Guid firingId = Guid.NewGuid();

        TriggerReviewContext context = new(
            trigger.Id,
            InstrumentId.Parse(trigger.Symbol),
            trigger.Indicator,
            trigger.Period,
            trigger.ResolutionMinutes,
            trigger.Comparison,
            trigger.Threshold,
            observed,
            now);

        // R-4 SUGGESTION THROTTLE (gh#551): decided BEFORE the reviewer, so a suppressed setup pays no LLM call and no
        // spend. Inert (Full, admits everything) unless opted in with a declared governor. Advisory, ahead of the
        // execution gate — a suggestion carries no risk, so this never substitutes for the take-time gate.
        SuggestionThrottleDecision throttle = await DecideThrottleAsync(database, trigger, now, cancellationToken);

        AgentReview review;
        if (throttle.IsSuppressed)
        {
            // The account's daily governor is reached, or its daily target is hit with stand-down on: no new entry. No
            // LLM call is made (the point: cap WHETHER a call happens) and, with no costs, no AIUsage row is written. A
            // fire is still a fire (journaled + arm advanced below), so the operator gets one "suggestions paused"
            // advisory per arming edge, carrying the throttle's own reason -- the honest-inert posture.
            review = new AgentReview(
                new ReviewOutcome.Suppress(ThrottleSuppress(throttle.Reason), throttle.Explanation), Costs: []);
        }
        else if (governorPass is not null
            && _governor.Evaluate(governorPass.Budget, governorPass.SpentUsd) is { IsBlocked: true } blocked)
        {
            // AI-SPEND GOVERNOR (gh#448) -- BUDGET EXHAUSTED: short-circuit BEFORE the reviewer. No LLM call is made
            // (the point: cap WHETHER a call happens) and, with no costs, no AIUsage row is written. A fire is still
            // a fire (journaled + arm advanced below), so the operator gets one "review paused, budget spent" advisory
            // per arming edge -- the honest-inert posture, exactly like NoReviewerConfigured / ReviewerUnavailable.
            review = new AgentReview(new ReviewOutcome.Suppress(SuppressReason.BudgetExhausted, blocked.Reason), Costs: []);
        }
        else
        {
            // DEEP-TIER ENRICHMENT (gh#476): assemble the numeric market context the escalated deep call may use and
            // attach it to the context BEFORE the reviewer wakes. Built eagerly here (we cannot know whether triage will
            // escalate) rather than lazily inside the reviewer, so the reviewer stays a pure judgment seam that never
            // depends on data access (gate-below-model, gh#402); only the deep render reads it, so a non-escalating fire
            // pays one indexed read and nothing else. FAIL-OPEN: a read fault leaves the context un-enriched (the deep
            // call, if it happens, uses the base render) -- enrichment adds context, it must never cost a fire.
            context = context with { Enrichment = await BuildEnrichmentAsync(context, cancellationToken) };

            // BUDGET-AWARE ESCALATION SKIP (gh#478): the pass-level governor above only caps WHETHER the review runs at
            // all; this caps whether the cheap triage may escalate to the expensive DEEP tier. Cheap triage fits the
            // remaining budget (we are past the IsBlocked check), but a full triage->deep PAIR might not -- the
            // partial-budget overrun ADR-0008 named. So decide affordability HERE (the scan holds the tally + the
            // budget) and pass the reviewer only a plain permission bit, keeping it pure of the governor (gh#449). The
            // reviewer reports its own conservative deep-call cost; we never tell it the budget. Null governor (inert or
            // a fail-open spend read) => un-gated, escalation allowed, exactly as before gh#478.
            bool allowEscalate = governorPass is null
                || governorPass.SpentUsd + _reviewer.EstimatedDeepCallCostUsd <= governorPass.Budget.DailyBudgetUsd;

            // The reviewer seam is fail-closed BY CONTRACT (LlmTriggerReviewer maps every unusable output to a
            // Suppress), but the seam admits any ITriggerReviewer -- a future provider-backed one can THROW on a
            // transient fault. A throw must debounce like every other outcome (one review attempt per arming edge, the
            // arm still advancing to Fired below), never escape to the per-owner guard: that would leave the arm Armed
            // and re-review every pass (unbounded LLM cost) AND roll back a co-owner's already-sent mechanical fire.
            try
            {
                review = await _reviewer.ReviewAsync(context, cancellationToken, allowEscalate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // A reviewer that itself threw (not the provider it wraps — LlmTriggerReviewer never throws) carries no
                // cost to ledger; treat it as unavailable, same debounce as any other outcome.
                _logger.LogError(error, "Agent-review trigger {Id} reviewer threw; treating as unavailable.", trigger.Id);
                review = new AgentReview(new ReviewOutcome.Suppress(SuppressReason.ReviewerUnavailable, "the reviewer threw"), Costs: []);
            }
        }

        // Ledger the LLM-call spend, stamped with THIS owner (the scan is the single tenancy authority) + the trace.
        // ONE row per billed call: empty when no call was made (inert reviewer, or a budget-exhausted short-circuit),
        // one for a single triage call, and TWO when the triage escalated to the deep tier (gh#449). The real ledger
        // is fail-open by its OWN contract, but the seam admits any IAiUsageLedger -- so guard it at the boundary too,
        // exactly as the reviewer and advisory-notify seams above/below are. Un-guarded, a contract-violating throw
        // would unwind to the per-owner catch and discard the whole pass: this fire AND any co-owner mechanical alert
        // already SENT earlier this pass (re-sent next pass -- a duplicate). A spend-bookkeeping fault must never do
        // that. The try/catch is INSIDE the loop so a fault on one row still records the next. Only the caller's own
        // cancellation escapes.
        string? traceId = Activity.Current?.TraceId.ToString();
        foreach (AiCallCost cost in review.Costs)
        {
            // Meter FIRST, outside the ledger's try (gh#477): the meter and the ledger are independent spend sinks, so
            // the export-only meter must record this call even if the durable ledger write then faults. A counter Add
            // does not throw, so it needs no guard; an escalated fire meters both the triage and the deep row.
            _llmMetrics.RecordLlmCall(cost);

            try
            {
                await _ledger.RecordAsync(new AiUsageEntry(owner, cost, traceId, now), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogError(
                    error, "AIUsage ledger record threw for owner {Owner}; the fire is committed regardless.", owner);
            }
        }

        // Accrue EVERY call's estimated cost to the pass tally so LATER agent-review fires this pass evaluate against
        // RISING spend -- the per-fire mirror of the risk gate consuming DayLoss. An escalated fire (gh#449) accrues
        // BOTH the triage and the deep cost. Accrue the ESTIMATE even if the ledger write faulted: over-counting
        // toward the cap is the safe direction for a budget, and the next pass re-reads the real floor fresh, so no
        // double-count persists. Empty Costs (a budget-exhausted short-circuit or the inert reviewer) accrues nothing.
        if (governorPass is not null)
        {
            foreach (AiCallCost cost in review.Costs)
            {
                governorPass.Accrue(cost.EstimatedCostUsd);
            }
        }

        switch (review.Outcome)
        {
            case ReviewOutcome.Suggest suggest:
                await StageSuggestionAsync(
                    database, trigger, owner, suggest, now, dedupKey, firingId, advisories, suggestionPushes, throttle, cancellationToken);
                break;

            case ReviewOutcome.Suppress { Reason: SuppressReason.NoReviewerConfigured }:
                // The honest inert reviewer: the operator is TOLD a setup fired that could not be reviewed.
                advisories.Add(new Notification(
                    NotificationSeverity.Notify,
                    $"Setup needs review — {TitleFor(trigger)}",
                    "A setup fired that needs agent review; no reviewer is configured yet.",
                    dedupKey));
                break;

            case ReviewOutcome.Suppress { Reason: SuppressReason.ReviewerUnavailable }:
                // A configured reviewer was tried and failed (a provider fault or an empty completion). Fail-closed but
                // NOT silent: tell the operator a setup fired that could not be reviewed, so an outage never quietly
                // eats fires. Bounded to one advisory per arming edge, since the arm still advances to Fired below.
                advisories.Add(new Notification(
                    NotificationSeverity.Notify,
                    $"Setup needs review — {TitleFor(trigger)}",
                    "A setup fired that needs agent review; the reviewer was unavailable. Review it manually.",
                    dedupKey));
                break;

            case ReviewOutcome.Suppress { Reason: SuppressReason.BudgetExhausted }:
                // The budget governor paused review BEFORE any call (gh#448). Fail-closed but NOT silent: tell the
                // operator a setup fired that could not be reviewed because the daily AI budget is spent. One advisory
                // per arming edge (the arm still advances to Fired below), mirroring ReviewerUnavailable.
                advisories.Add(new Notification(
                    NotificationSeverity.Notify,
                    $"Setup needs review — {TitleFor(trigger)}",
                    "A setup fired but agent review is paused: the daily AI-spend budget is reached. Review it manually.",
                    dedupKey));
                break;

            case ReviewOutcome.Suppress { Reason: SuppressReason.GovernorReached or SuppressReason.DailyTargetStandDown } throttled:
                // R-4 THROTTLE (gh#551): a new entry was suppressed because the account's daily governor is reached or
                // its target stand-down is on. Never silent -- the operator is told why suggestions paused, quoting the
                // throttle's own explanation, so silence is never mistaken for "nothing is setting up". One per arming edge.
                advisories.Add(new Notification(
                    NotificationSeverity.Notify,
                    $"Suggestions paused — {TitleFor(trigger)}",
                    throttled.Detail,
                    dedupKey));
                break;

            case ReviewOutcome.Suppress { Reason: SuppressReason.EscalationDeclined }:
                // BUDGET-AWARE ESCALATION SKIP (gh#478): triage reviewed the setup and judged it hard enough to want the
                // deep tier, but the remaining budget could not afford the deeper look, so it was withheld. Fail-closed
                // but NOT silent -- the operator is told a setup fired that a quick pass flagged for deeper analysis it
                // could not get, and should review manually. The scan is the one that knows the reason is BUDGET (the
                // reviewer only got a neutral bit), so the budget framing is set here. One advisory per arming edge.
                advisories.Add(new Notification(
                    NotificationSeverity.Notify,
                    $"Setup needs review — {TitleFor(trigger)}",
                    "A setup fired and a quick review flagged it for deeper analysis, but the daily AI-spend budget "
                    + "could not afford the deeper look. Review it manually.",
                    dedupKey));
                break;

            case ReviewOutcome.Suppress { Reason: SuppressReason.NotWorthSurfacing }:
                // A legitimate, silent outcome: the agent reviewed it and judged it not worth surfacing.
                break;

            case ReviewOutcome.Suppress suppress:
                // MalformedOutput or InvalidGeometry: logged, no operator notify (fail-closed, not fail-loud).
                _logger.LogWarning(
                    "Agent-review trigger {Id} produced no suggestion ({Reason}): {Detail}",
                    trigger.Id,
                    suppress.Reason,
                    suppress.Detail);
                break;
        }

        // A fire is a fire: journal it and keep the arm at Fired for EVERY agent-review outcome, suggest or suppress.
        JournalFiring(database, trigger, owner, observed, now, dedupKey, firingId);
        trigger.LastFiredAt = now;
    }

    /// <summary>
    /// Builds the deep-tier enrichment for a fire, FAIL-OPEN (gh#476): a read fault (or a contract-violating throw from
    /// the seam) logs and returns <see langword="null"/>, so the review runs on the un-enriched context rather than
    /// aborting the fire. Only the caller's own cancellation escapes — that is our shutdown, not a review outcome.
    /// </summary>
    private async Task<ReviewEnrichment?> BuildEnrichmentAsync(TriggerReviewContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await _enrichmentSource.BuildAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error, "Deep-tier enrichment could not be assembled for trigger {Id}; reviewing un-enriched.", context.TriggerId);
            return null;
        }
    }

    /// <summary>Validates the proposal's geometry, checks the account is tradable, and stages the suggestion + advisory.</summary>
    private async Task StageSuggestionAsync(
        TradingCopilotDbContext database,
        TriggerRecord trigger,
        Guid owner,
        ReviewOutcome.Suggest suggest,
        DateTimeOffset now,
        string dedupKey,
        Guid firingId,
        List<Notification> advisories,
        List<RealtimeSuggestion> suggestionPushes,
        SuggestionThrottleDecision throttle,
        CancellationToken cancellationToken)
    {
        // The validity window (gh#544): the operator's configured span, clamped so the suggestion cannot outlive this
        // market's auto-flatten deadline -- a live suggestion past the deadline invites a position the flatten is
        // about to close. The system's value, never the model's, exactly like Size and Mode below.
        DateTimeOffset expiresAt = SuggestionValidity.ExpiresAt(
            now, _suggestionValidity, _deadlines.DeadlineFor(InstrumentId.Parse(trigger.Symbol)));

        // Layer two below the reviewer: pure geometry sanity. A malformed / hostile proposal that got past the
        // reviewer is rejected HERE before it can be persisted -- treated as Suppress(InvalidGeometry): log, no
        // suggestion. (The risk gate is the true backstop below this, at take-time.)
        string? geometryError = SuggestionGeometry.Validate(
            suggest.Side, suggest.EntryPrice, suggest.StopPrice, suggest.TargetPrice);
        if (geometryError is not null)
        {
            _logger.LogWarning(
                "Agent-review trigger {Id} proposed incoherent geometry ({Reason}); no suggestion staged.",
                trigger.Id,
                geometryError);
            return;
        }

        // Mode is read LIVE from the account, never stored on the trigger. An account that vanished, or is undeclared,
        // cannot be traded -- so nothing is suggested on it (the InvalidGeometry posture: log, no suggestion).
        Account? account = await database.Accounts
            .FirstOrDefaultAsync(candidate => candidate.Id == trigger.AccountId, cancellationToken);
        if (account is null || account.Mode == TradingMode.Undeclared)
        {
            _logger.LogWarning(
                "Agent-review trigger {Id} has no tradable account (missing or undeclared mode); no suggestion staged.",
                trigger.Id);
            return;
        }

        // R-4 THROTTLE — the binding deterministic arm (gh#551). While throttled, the per-window cap (from headroom
        // ALONE) and the conviction floor drop lower-conviction candidates; a Full / inert decision admits everything.
        // Confidence is a FILTER only: it can drop a candidate below the floor, but it can NEVER lift the
        // headroom-derived cap — the model cannot inflate its own number past the throttle. Checked HERE, before the
        // supersede below, so a dropped candidate leaves the incumbent standing rather than voiding it for nothing.
        // Counted over THIS account's Central trading day, the same window the headroom read uses; the Full path skips
        // the count so opt-out stays a byte-for-byte no-op.
        if (throttle.Mode != SuggestionThrottleMode.Full)
        {
            DateTimeOffset dayStart = CentralDayStartUtc(now);

            // Committed suggestions for this account today (the R-20-scoped store)...
            int issuedInWindow = await database.Suggestions
                .Where(candidate => candidate.AccountId == trigger.AccountId && candidate.CreatedAt >= dayStart)
                .CountAsync(cancellationToken);

            // ...PLUS suggestions already STAGED earlier in THIS pass but not yet committed. The scan commits once at the
            // end of the per-owner loop (the gh#455 shared transaction), so a committed-only count reads a stale total
            // and several same-account agent-review fires in one pass would each slip under the cap — defeating "issue
            // fewer" in the very depleting-headroom regime the throttle exists for. Counting the change-tracker's pending
            // Added rows is the per-pass mirror of the AI-spend governor's within-pass tally, so the Nth fire this pass
            // sees the N-1 already staged. ADDED only: a superseded incumbent is Modified and a loaded incumbent is
            // Unchanged, so neither double-counts against the committed query above.
            issuedInWindow += database.ChangeTracker.Entries<Suggestion>()
                .Count(entry => entry.State == EntityState.Added
                    && entry.Entity.AccountId == trigger.AccountId
                    && entry.Entity.CreatedAt >= dayStart);

            if (!throttle.Admits(issuedInWindow, suggest.Confidence))
            {
                _logger.LogInformation(
                    "R-4 throttle: agent-review trigger {Id} suggestion not admitted (issued {Issued} of cap {Cap}; "
                    + "confidence {Confidence} vs floor {Floor}); not staged.",
                    trigger.Id, issuedInWindow, throttle.PerWindowCap, suggest.Confidence, throttle.RequiredConfidence);
                return;
            }
        }

        // SUPERSEDE (gh#550, R-4, ADR-0013): a re-formed setup issues a NEW, superseding suggestion rather than
        // resurrecting the prior one -- ADR-0013 forbids chasing price, and issuing a superseding row IS the
        // sanctioned alternative. Find the live incumbent from the SAME trigger + instrument + side. Keyed on the
        // TRIGGER IDENTITY -- the incumbent's originating firing's TriggerId (a Suggestion carries no TriggerId; the
        // firing link is its trigger provenance, gh#542) -- NEVER the symbol, so two DIFFERENT triggers on one
        // symbol+side (different indicator/period/threshold) never destroy each other. Undispositioned only: an
        // incumbent the operator already acted on is journal evidence, left untouched (a new independent row issues
        // instead). Non-terminal only (Active/Stale): a terminal row is never revived.
        //
        // SINGLE-INCUMBENT IS ENFORCED HERE, IN APP CODE -- deliberately NOT a partial unique index (gh#455). This row
        // is staged into the scan pass's SHARED DbContext alongside the arm-state transition, the TriggerFiringRecord
        // and the outbox advisory, all committed by ONE SaveChangesAsync; a unique-index violation would abort that
        // whole commit and lose the firing journal + arm transition (a constraint backstops only its transaction's
        // owner). Each issuance voids the prior head, so at most one non-terminal head per (trigger, side) ever exists;
        // OrderByDescending(Version) is defensive should that invariant ever be dented.
        //
        // SAFE ONLY UNDER A SINGLE SEQUENTIAL WRITER (gh#617). "At most one live incumbent" borrows a guarantee this
        // method neither states nor enforces: TriggerScanHost is one BackgroundService whose loop AWAITS each pass, so
        // ScanAsync never overlaps itself; owners and triggers are walked in sequential foreach loops committed by one
        // SaveChangesAsync; and TriggerDebounce lets a given trigger fire at most once per pass. Break any of those --
        // a second app instance, a manual "run scan now" endpoint, or parallelising the owner/trigger loop -- and two
        // passes could both read "no incumbent" here and both insert, minting two live heads with an identical Version
        // and no error. Anything that adds a concurrent scan writer must re-establish this invariant at that point (a
        // serializable transaction or a per-(trigger, side) advisory lock -- NOT the gh#455 unique index). Tracked by gh#617.
        Suggestion? incumbent = await database.Suggestions
            .Where(candidate => candidate.State == SuggestionState.Active || candidate.State == SuggestionState.Stale)
            .Where(candidate => candidate.Instrument == trigger.Symbol && candidate.Side == suggest.Side)
            .Where(candidate => candidate.TriggerFiringId != null
                && database.TriggerFirings.Any(firing =>
                    firing.Id == candidate.TriggerFiringId && firing.TriggerId == trigger.Id))
            .Where(candidate => !database.SuggestionDispositions.Any(disposition => disposition.SuggestionId == candidate.Id))
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefaultAsync(cancellationToken);

        int version = 1;
        Guid? supersedesId = null;
        if (incumbent is not null)
        {
            // A GUARDED one-way transition to ExpiredVoid: the query above already pinned the incumbent to a
            // non-terminal state, so this void is guarded-by-construction -- a terminal row is never rewritten. This
            // is the THIRD writer to Suggestion.State (issuance and the gh#545 expiry sweep are the others);
            // consolidate onto #545's transition helper when it lands. ONLY State changes -- the incumbent's trade
            // parameters are immutable once issued (R-4), the invariant the journal depends on.
            incumbent.State = SuggestionState.ExpiredVoid;
            version = incumbent.Version + 1;
            supersedesId = incumbent.Id;

            // gh#684: the incumbent is now terminal -- queue its transition so the owner's card surface clears it. The
            // push fires only after the commit below; a rolled-back pass never reaches the flush, so this is never seen.
            suggestionPushes.Add(new RealtimeSuggestion(incumbent.Id, SuggestionState.ExpiredVoid.ToString(), now));
        }

        // Size from the TRIGGER (the operator's), mode LIVE from the account -- never the model's. The `!` are sound:
        // an agent-review trigger carries a non-null account + size (the endpoint validation + the DB check).
        Guid suggestionId = Guid.NewGuid(); // hoisted (gh#684) so the realtime push can cite the id that was staged
        database.Suggestions.Add(new Suggestion
        {
            Id = suggestionId,
            UserId = owner,
            AccountId = trigger.AccountId!.Value,
            Instrument = trigger.Symbol,
            Side = suggest.Side,
            Size = trigger.Size!.Value,
            EntryPrice = suggest.EntryPrice,
            StopPrice = suggest.StopPrice,
            TargetPrice = suggest.TargetPrice,
            Mode = account.Mode,
            State = SuggestionState.Active,
            CreatedAt = now,

            // The model's prose, now durable (gh#542) -- it was previously generated, billed and discarded. Capped and
            // validated at the reviewer's parse boundary, so anything unusable never reached here.
            Rationale = suggest.Rationale,

            // The cited signal (gh#542): a soft link to the firing, plus the indicator identity COPIED, because
            // indicator/period/resolution live on the mutable, deletable TriggerRecord and R-4 needs the citation to
            // stay readable after the trigger is edited or deleted.
            TriggerFiringId = firingId,
            CitedIndicator = trigger.Indicator,
            CitedPeriod = trigger.Period,
            CitedResolutionMinutes = trigger.ResolutionMinutes,

            // Display only (gh#543) -- it changes nothing about size, geometry or whether this row is written.
            Confidence = suggest.Confidence,

            // The system's window (gh#544), clamped so it cannot outlive this market's auto-flatten deadline.
            ExpiresAt = expiresAt,

            // The supersede spine (gh#550): a first issuance is Version 1 superseding nothing; a re-formed setup is
            // one version higher and links to the incumbent it just voided above.
            Version = version,
            SupersedesId = supersedesId,
        });

        // gh#684: the new row is staged -- queue its arrival so the owner's card surface adds it after the commit.
        suggestionPushes.Add(new RealtimeSuggestion(suggestionId, SuggestionState.Active.ToString(), now));

        // Queue the advisory for the post-commit flush; the suggestion is the durable artifact behind it. The expiry
        // rides along in market wall-clock (gh#544), so the operator learns the window on the channel that already
        // reaches them rather than only in the app.
        advisories.Add(new Notification(
            NotificationSeverity.Notify,
            $"Reviewed setup available — {TitleFor(trigger)}",
            $"The agent reviewed a fired setup on {trigger.Symbol} and proposed a {SideWord(suggest.Side)} entry at "
            + $"{suggest.EntryPrice} (stop {suggest.StopPrice}, target {suggest.TargetPrice}). "
            + FormattableString.Invariant($"Valid until {MarketClock.ToMarketTime(expiresAt):HH:mm} CT."),
            dedupKey));
    }

    /// <summary>
    /// Pushes one suggestion lifecycle change to the owning operator's realtime connections, best-effort (gh#684).
    /// Mirrors the advisory flush and the gh#683 account notifier: a hub fault is logged and swallowed so it can
    /// never fail or roll back the write that already committed. Only the caller's own cancellation escapes.
    /// </summary>
    private async Task NotifySafelyAsync(Guid owner, RealtimeSuggestion push, CancellationToken cancellationToken)
    {
        try
        {
            await _suggestionNotifier.SuggestionChangedAsync(owner, push, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Realtime suggestion push for {SuggestionId} ({State}) failed for owner {Owner}; the write is committed regardless.",
                push.SuggestionId, push.State, owner);
        }
    }

    private static void JournalFiring(
        TradingCopilotDbContext database,
        TriggerRecord trigger,
        Guid owner,
        decimal observed,
        DateTimeOffset now,
        string dedupKey,
        Guid firingId) =>
        database.TriggerFirings.Add(new TriggerFiringRecord
        {
            Id = firingId,
            UserId = owner,
            TriggerId = trigger.Id,
            FiredAt = now,
            ObservedValue = observed,
            Threshold = trigger.Threshold,
            Comparison = trigger.Comparison,
            DedupKey = dedupKey,
        });

    // The daily spend window resets at CENTRAL-trading-day midnight (mirroring the daily risk governor + auto-flatten,
    // which use MarketClock precisely because a UTC date splits a live CME session), converted to UTC for the
    // OccurredAt comparison. Midnight is never in the DST spring-forward gap, so no invalid-local-time guard is needed.
    private static DateTimeOffset CentralDayStartUtc(DateTimeOffset now)
    {
        DateTime centralMidnight = DateTime.SpecifyKind(MarketClock.ToMarketTime(now).Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(centralMidnight, MarketClock.CentralTime.GetUtcOffset(centralMidnight)).ToUniversalTime();
    }

    private static string ThresholdDedupKey(DateTimeOffset now) =>
        FormattableString.Invariant($"ai-spend:threshold:{MarketClock.ToMarketTime(now):yyyy-MM-dd}");

    private static string DedupKeyFor(TriggerRecord trigger) => $"trigger:{trigger.Id}:{trigger.ArmCycle}";

    private static string TitleFor(TriggerRecord trigger) =>
        $"{trigger.Indicator.ToUpperInvariant()}({trigger.Period}) {trigger.ResolutionMinutes}m "
        + $"{Word(trigger.Comparison)} {trigger.Threshold} on {trigger.Symbol}";

    private static string BodyFor(TriggerRecord trigger, decimal observed) =>
        $"{trigger.Indicator.ToUpperInvariant()}({trigger.Period}) on {trigger.Symbol} is {observed} "
        + $"({Word(trigger.Comparison)} {trigger.Threshold}, {trigger.ResolutionMinutes}m).";

    private static string Word(IndicatorComparison comparison) => comparison switch
    {
        IndicatorComparison.Below => "below",
        IndicatorComparison.Above => "above",
        _ => "unknown",
    };

    private static string SideWord(OrderSide side) => side switch
    {
        OrderSide.Buy => "long",
        OrderSide.Sell => "short",
        _ => "unknown",
    };

    /// <summary>
    /// The per-pass AI-spend tally (gh#448): seeded from the once-per-pass ledger read, then incremented by each
    /// authorized agent-review fire's estimated cost, so later fires in the same pass evaluate against rising spend —
    /// the per-fire mirror of the risk gate consuming the day's loss. Single-threaded within a pass, so no locking.
    /// </summary>
    private sealed class GovernorPass(AiSpendBudget budget, decimal spentAtStart)
    {
        /// <summary>The operator's daily budget for the pass.</summary>
        public AiSpendBudget Budget { get; } = budget;

        /// <summary>Spend so far this window — the ledger floor at pass start plus each fire accrued since.</summary>
        public decimal SpentUsd { get; private set; } = spentAtStart;

        /// <summary>Accrues one authorized call's estimated cost.</summary>
        /// <param name="costUsd">The call's estimated USD cost.</param>
        public void Accrue(decimal costUsd) => SpentUsd += costUsd;
    }
}
