using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The deterministic trigger scan's core (gh#385, A1): evaluate every enabled mechanical-alert trigger over the
/// pre-computed indicators and, on the arming edge, fire a mechanical alert (ADR-0008 — the LLM is never in this
/// loop). It <b>notifies; it never places an order</b> (enforcement stays below the model).
/// </summary>
/// <remarks>
/// <para>
/// Background plumbing with no request user: it <b>discovers</b> owners with enabled triggers via
/// <c>IgnoreQueryFilters</c> (the conditional-firing pattern), then does the work for each owner in a DbContext
/// <b>scoped to that owner</b>, so every firing row keeps its owner (R-20). Indicator reads go through the global
/// <see cref="IIndicatorSource"/> — a missing value is <see langword="null"/> and the condition treats it as
/// unmeasurable, so a trigger never fires on absence (fail-closed, gh#311).
/// </para>
/// <para>
/// Firing is <b>commit-then-notify</b>: the arm-state change and the <see cref="TriggerFiringRecord"/> commit first
/// (the durable record of the incident, unique per <c>(trigger, armCycle)</c>), and only a <i>successful</i> commit
/// releases the alert. A failed commit therefore sends nothing and the trigger re-fires cleanly on the next scan —
/// no missed page from a transient fault, and no double page from a rolled-back send. (Alert <i>delivery</i> is
/// still best-effort via the in-process notification pump; a durable notification outbox that closes the narrow
/// crash-between-commit-and-enqueue window is a cross-cutting follow-on, gh#400.)
/// </para>
/// </remarks>
public sealed class TriggerEvaluationService
{
    private readonly TradingCopilotDbContext _discovery;
    private readonly DbContextOptions<TradingCopilotDbContext> _options;
    private readonly IIndicatorSource _indicators;
    private readonly INotificationChannel _notifications;
    private readonly ILogger<TriggerEvaluationService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used only to discover which owners have enabled triggers.</param>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context for the work.</param>
    /// <param name="indicators">The indicator read seam (global; a missing value is null).</param>
    /// <param name="notifications">The alert channel (the firing's mechanical output).</param>
    /// <param name="logger">The logger.</param>
    public TriggerEvaluationService(
        TradingCopilotDbContext discovery,
        DbContextOptions<TradingCopilotDbContext> options,
        IIndicatorSource indicators,
        INotificationChannel notifications,
        ILogger<TriggerEvaluationService> logger)
    {
        _discovery = discovery;
        _options = options;
        _indicators = indicators;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>Runs one scan pass over every enabled mechanical-alert trigger.</summary>
    /// <param name="now">The instant the pass evaluates against (the indicator <c>asOf</c> and firing timestamp).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many triggers fired this pass.</returns>
    public async Task<int> EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Discover owners with enabled mechanical-alert triggers -- background, so the R-20 filter is bypassed for
        // discovery only. Route is honored here AND in the per-owner query below, so a non-mechanical route can
        // never mechanical-alert even if one were somehow persisted.
        List<Guid> owners = await _discovery.Triggers
            .IgnoreQueryFilters()
            .Where(trigger => trigger.Enabled && trigger.Route == TriggerRoute.MechanicalAlert)
            .Select(trigger => trigger.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (owners.Count == 0)
        {
            return 0;
        }

        int fired = 0;
        foreach (Guid owner in owners)
        {
            fired += await EvaluateOwnerAsync(owner, now, cancellationToken);
        }

        return fired;
    }

    private async Task<int> EvaluateOwnerAsync(Guid owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Per-owner context: reads and writes are R-20-scoped, so every firing row keeps its owner.
        await using TradingCopilotDbContext database = new(_options, new OwnerUser(owner));

        List<Trigger> triggers = await database.Triggers
            .Where(trigger => trigger.Enabled && trigger.Route == TriggerRoute.MechanicalAlert)
            .ToListAsync(cancellationToken);

        List<PendingAlert> pending = [];
        foreach (Trigger trigger in triggers)
        {
            // One bad trigger must not cost the rest of the owner's watch its pass.
            try
            {
                PendingAlert? alert = StageOne(database, trigger, await ValueForAsync(trigger, now, cancellationToken), now);
                if (alert is not null)
                {
                    pending.Add(alert.Value);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Trigger {TriggerId} evaluation failed; the next scan retries.", trigger.Id);
            }
        }

        if (!database.ChangeTracker.HasChanges())
        {
            return 0;
        }

        // Commit BEFORE notifying: a failed commit releases no alert and rolls the arm state back, so the trigger
        // re-fires cleanly next scan (no missed page from a transient fault, no double page from a rolled-back
        // send). One owner's commit fault is contained here, never abandoning the others in the pass.
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Trigger firings for owner {Owner} failed to commit; the next scan retries.", owner);
            return 0;
        }

        int fired = 0;
        foreach (PendingAlert alert in pending)
        {
            if (alert.Notification is not null)
            {
                await _notifications.SendAsync(alert.Notification, cancellationToken);
                fired++;
            }
            else
            {
                await _notifications.ResolveAsync(alert.DedupKey, cancellationToken);
            }
        }

        return fired;
    }

    private async Task<decimal?> ValueForAsync(Trigger trigger, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await _indicators.GetValueAsync(
            InstrumentId.Parse(trigger.Instrument), trigger.Indicator, trigger.Period, trigger.ResolutionMinutes, now,
            cancellationToken);
    }

    private static PendingAlert? StageOne(TradingCopilotDbContext database, Trigger trigger, decimal? value, DateTimeOffset now)
    {
        IndicatorThresholdCondition condition = new(
            InstrumentId.Parse(trigger.Instrument), trigger.Indicator, trigger.Period, trigger.ResolutionMinutes,
            trigger.Comparison, trigger.Threshold);

        ConditionEvaluation evaluation = condition.Evaluate(value);
        TriggerDecision decision = TriggerDebounce.Decide(trigger.ArmState, evaluation);

        // Nothing to persist: a hold, or a debounced repeat.
        if (decision.Action == TriggerAction.None && decision.NextState == trigger.ArmState)
        {
            return null;
        }

        string dedupKey = $"trigger:{trigger.Id}:{trigger.ArmCycle}";
        PendingAlert? pending = null;

        if (decision.Action == TriggerAction.Fire)
        {
            // Satisfied implies a measured value, so value is non-null here.
            decimal measured = value!.Value;
            database.TriggerFirings.Add(new TriggerFiringRecord
            {
                Id = Guid.NewGuid(),
                UserId = trigger.UserId,
                TriggerId = trigger.Id,
                ArmCycle = trigger.ArmCycle,
                Instrument = trigger.Instrument,
                Indicator = trigger.Indicator,
                Period = trigger.Period,
                ResolutionMinutes = trigger.ResolutionMinutes,
                Comparison = trigger.Comparison,
                Threshold = trigger.Threshold,
                Value = measured,
                FiredAt = now,
            });
            trigger.LastFiredAt = now;
            pending = new PendingAlert(dedupKey, new Notification(
                NotificationSeverity.Notify, TitleFor(trigger), BodyFor(trigger, measured), dedupKey));
        }
        else if (decision.Action == TriggerAction.Resolve)
        {
            // Re-arm: resolve the current incident, then bump the cycle so the next crossing is a distinct one.
            pending = new PendingAlert(dedupKey, Notification: null);
            trigger.ArmCycle++;
        }

        trigger.ArmState = decision.NextState;
        return pending;
    }

    private static string TitleFor(Trigger trigger) => $"Trigger fired: {Describe(trigger)}";

    private static string BodyFor(Trigger trigger, decimal value) =>
        $"{trigger.Indicator.ToUpperInvariant()}({trigger.Period}) on {trigger.Instrument} @ {trigger.ResolutionMinutes}m "
        + $"is {value} — {Word(trigger.Comparison)} {trigger.Threshold}.";

    private static string Describe(Trigger trigger) =>
        $"{trigger.Indicator.ToUpperInvariant()}({trigger.Period}) {Word(trigger.Comparison)} {trigger.Threshold} "
        + $"on {trigger.Instrument} @ {trigger.ResolutionMinutes}m";

    private static string Word(ThresholdComparison comparison) =>
        comparison == ThresholdComparison.Below ? "at or below" : "at or above";

    /// <summary>A staged side effect, released only after the arm-state commit succeeds.</summary>
    /// <param name="DedupKey">The incident key.</param>
    /// <param name="Notification">The alert to send on a fire; <see langword="null"/> means resolve the incident (re-arm).</param>
    private readonly record struct PendingAlert(string DedupKey, Notification? Notification);

    /// <summary>The owning operator, so every trigger read and firing write is R-20-scoped to it.</summary>
    private sealed record OwnerUser(Guid UserId) : ICurrentUser;
}
