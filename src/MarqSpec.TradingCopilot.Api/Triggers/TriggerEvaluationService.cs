using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
/// </remarks>
public sealed class TriggerEvaluationService
{
    private readonly TradingCopilotDbContext _discovery;
    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly IIndicatorSource _indicators;
    private readonly INotificationChannel _notifications;
    private readonly ITriggerReviewer _reviewer;
    private readonly ILogger<TriggerEvaluationService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used only to discover which owners have enabled triggers.</param>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context for the work.</param>
    /// <param name="indicators">The global read seam for pre-computed indicator values (R-22).</param>
    /// <param name="notifications">The alerting seam (ADR-0019); both routes send through it.</param>
    /// <param name="reviewer">
    /// The agent-review judgment seam (ADR-0008): the agent-review route wakes it once per fire. It only <b>proposes</b>
    /// — it is not, and must not become, a path to execution (enforcement lives below the model).
    /// </param>
    /// <param name="logger">The logger.</param>
    public TriggerEvaluationService(
        TradingCopilotDbContext discovery,
        DbContextOptions<TradingCopilotDbContext> options,
        IIndicatorSource indicators,
        INotificationChannel notifications,
        ITriggerReviewer reviewer,
        ILogger<TriggerEvaluationService> logger)
    {
        _discovery = discovery;
        _options = options;
        _indicators = indicators;
        _notifications = notifications;
        _reviewer = reviewer;
        _logger = logger;
    }

    /// <summary>Evaluates every enabled mechanical trigger and fires the crossing edges.</summary>
    /// <param name="now">The moment to evaluate as of — supplied by the caller; the service never reads a clock.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many triggers fired this pass.</returns>
    public async Task<int> ScanAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Discover owners with enabled mechanical OR agent-review triggers -- background, so the R-20 filter is
        // bypassed here.
        List<Guid> owners = await _discovery.Triggers
            .IgnoreQueryFilters()
            .Where(trigger => trigger.Enabled
                && (trigger.Route == TriggerRoute.Mechanical || trigger.Route == TriggerRoute.AgentReview))
            .Select(trigger => trigger.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (owners.Count == 0)
        {
            return 0;
        }

        int fires = 0;
        foreach (Guid owner in owners)
        {
            try
            {
                fires += await ProcessOwnerAsync(owner, now, cancellationToken);
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

        if (fires > 0)
        {
            _logger.LogInformation("Trigger scan fired {Count} trigger(s).", fires);
        }

        return fires;
    }

    /// <summary>Evaluates one owner's enabled mechanical + agent-review triggers in a per-owner (R-20-scoped) context.</summary>
    private async Task<int> ProcessOwnerAsync(Guid owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Per-owner context: the R-20 filter applies, so every trigger read and every firing / suggestion written is
        // this owner's alone -- one owner's SaveChanges can never persist another's rows.
        await using TradingCopilotDbContext database = new(_options, new OwnerUser(owner));

        List<TriggerRecord> triggers = await database.Triggers
            .Where(trigger => trigger.Enabled
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

            if (decision.ShouldFire && trigger.Route == TriggerRoute.Mechanical)
            {
                // MECHANICAL: send-before-commit, UNCHANGED. The `!` is sound -- a fire needs a measured, satisfied
                // reading, so the value is non-null here.
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
                await FireAgentReviewAsync(database, trigger, owner, value!.Value, now, advisories, cancellationToken);
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
    /// The agent-review fire: wake the reviewer, stage a suggestion (or an advisory) per the outcome, and journal the
    /// firing regardless. NOTHING here reaches execution — it persists a suggestion and at most queues an advisory
    /// (enforcement lives below the model).
    /// </summary>
    private async Task FireAgentReviewAsync(
        TradingCopilotDbContext database,
        TriggerRecord trigger,
        Guid owner,
        decimal observed,
        DateTimeOffset now,
        List<Notification> advisories,
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

        ReviewOutcome outcome = await _reviewer.ReviewAsync(context, cancellationToken);
        switch (outcome)
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

    /// <summary>The owning operator, so every per-owner read and every journaled firing stays R-20-scoped.</summary>
    private sealed record OwnerUser(Guid UserId) : ICurrentUser;
}
