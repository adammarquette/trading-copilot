using System.Diagnostics;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
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
    private readonly IAiSpendGovernor _governor;
    private readonly AiSpendBudget? _budget;
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
    /// <param name="governor">
    /// The platform-level AI-spend gate (gh#448): a pure, deterministic budget check consulted before each agent-review
    /// LLM call. It gates <b>whether</b> a call is made (cost), never what it proposes (enforcement lives below the model).
    /// </param>
    /// <param name="governorOptions">The governor's budget config; absent (null budget) leaves the governor inert.</param>
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
        IAiSpendGovernor governor,
        IOptions<GovernorOptions> governorOptions,
        ILogger<TriggerEvaluationService> logger)
    {
        ArgumentNullException.ThrowIfNull(governorOptions);

        _discovery = discovery;
        _options = options;
        _indicators = indicators;
        _notifications = notifications;
        _reviewer = reviewer;
        _enrichmentSource = enrichmentSource;
        _ledger = ledger;
        _llmMetrics = metrics;
        _governor = governor;
        _budget = governorOptions.Value.ToBudget(); // null == inert (no cap configured); computed once per pass
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
            TriggerStaleness staleness = TriggerStaleness.Track(trigger.UnmeasurableSince, satisfaction, now);
            trigger.UnmeasurableSince = staleness.UnmeasurableSince;

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
                await FireAgentReviewAsync(database, trigger, owner, value!.Value, now, advisories, governorPass, cancellationToken);
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

        JournalFiring(database, trigger, owner, observed, now, dedupKey);
        trigger.LastFiredAt = now;
        return true;
    }

    /// <summary>
    /// The agent-review fire: consult the spend governor, wake the reviewer (unless the budget is spent), stage a
    /// suggestion (or an advisory) per the outcome, and journal the firing regardless. NOTHING here reaches execution
    /// — it persists a suggestion and at most queues an advisory (enforcement lives below the model).
    /// </summary>
    private async Task FireAgentReviewAsync(
        TradingCopilotDbContext database,
        TriggerRecord trigger,
        Guid owner,
        decimal observed,
        DateTimeOffset now,
        List<Notification> advisories,
        GovernorPass? governorPass,
        CancellationToken cancellationToken)
    {
        string dedupKey = DedupKeyFor(trigger);

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

        AgentReview review;
        if (governorPass is not null
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
                await StageSuggestionAsync(database, trigger, owner, suggest, now, dedupKey, advisories, cancellationToken);
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
        JournalFiring(database, trigger, owner, observed, now, dedupKey);
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
        List<Notification> advisories,
        CancellationToken cancellationToken)
    {
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

        // Size from the TRIGGER (the operator's), mode LIVE from the account -- never the model's. The `!` are sound:
        // an agent-review trigger carries a non-null account + size (the endpoint validation + the DB check).
        database.Suggestions.Add(new Suggestion
        {
            Id = Guid.NewGuid(),
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
        });

        // Queue the advisory for the post-commit flush; the suggestion is the durable artifact behind it.
        advisories.Add(new Notification(
            NotificationSeverity.Notify,
            $"Reviewed setup available — {TitleFor(trigger)}",
            $"The agent reviewed a fired setup on {trigger.Symbol} and proposed a {SideWord(suggest.Side)} entry at "
            + $"{suggest.EntryPrice} (stop {suggest.StopPrice}, target {suggest.TargetPrice}).",
            dedupKey));
    }

    private static void JournalFiring(
        TradingCopilotDbContext database, TriggerRecord trigger, Guid owner, decimal observed, DateTimeOffset now, string dedupKey) =>
        database.TriggerFirings.Add(new TriggerFiringRecord
        {
            Id = Guid.NewGuid(),
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
