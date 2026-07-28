using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
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
    private readonly ILogger<TriggerEvaluationService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="discovery">The scoped context, used only to discover which owners have enabled mechanical triggers.</param>
    /// <param name="options">The context options, used to build a per-owner (R-20-scoped) context for the work.</param>
    /// <param name="indicators">The global read seam for pre-computed indicator values (R-22).</param>
    /// <param name="notifications">The alerting seam (ADR-0019); the mechanical route sends through it.</param>
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

    /// <summary>Evaluates every enabled mechanical trigger and fires the crossing edges.</summary>
    /// <param name="now">The moment to evaluate as of — supplied by the caller; the service never reads a clock.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many triggers fired this pass.</returns>
    public async Task<int> ScanAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Discover owners with enabled mechanical triggers -- background, so the R-20 filter is bypassed here.
        List<Guid> owners = await _discovery.Triggers
            .IgnoreQueryFilters()
            .Where(trigger => trigger.Enabled && trigger.Route == TriggerRoute.Mechanical)
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
            _logger.LogInformation("Trigger scan fired {Count} mechanical alert(s).", fires);
        }

        return fires;
    }

    /// <summary>Evaluates one owner's enabled mechanical triggers in a per-owner (R-20-scoped) context.</summary>
    private async Task<int> ProcessOwnerAsync(Guid owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Per-owner context: the R-20 filter applies, so every trigger read and every firing written is this
        // owner's alone -- one owner's SaveChanges can never persist another's rows.
        await using TradingCopilotDbContext database = new(_options, new OwnerUser(owner));

        List<TriggerRecord> triggers = await database.Triggers
            .Where(trigger => trigger.Enabled && trigger.Route == TriggerRoute.Mechanical)
            .ToListAsync(cancellationToken);

        // Cache the global indicator read per series for the pass: many triggers can share one series, and the
        // read is a pure function of `now`.
        Dictionary<(string Symbol, string Indicator, int Period, int Resolution), decimal?> cache = new();
        int fires = 0;
        bool changed = false;

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

            if (decision.ShouldFire)
            {
                string dedupKey = DedupKeyFor(trigger);

                // SEND-BEFORE-COMMIT: emit BEFORE the SaveChanges that commits Fired. The channel returns
                // false -- it never throws, per INotificationChannel's contract -- when it could not accept the
                // alert (a wedged transport filling the bounded queue). Treat that as NON-DELIVERY: leave the
                // trigger ARMED so the next pass re-attempts the arming edge, rather than committing a
                // fired-but-unsent alert that would then debounce and be lost. The value is non-null here (a fire
                // needs a measured, satisfied reading), so the `!` is sound.
                bool sent = await _notifications.SendAsync(
                    new Notification(trigger.Severity, TitleFor(trigger), BodyFor(trigger, value!.Value), dedupKey),
                    cancellationToken);
                if (sent)
                {
                    database.TriggerFirings.Add(new TriggerFiringRecord
                    {
                        Id = Guid.NewGuid(),
                        UserId = owner,
                        TriggerId = trigger.Id,
                        FiredAt = now,
                        ObservedValue = value!.Value,
                        Threshold = trigger.Threshold,
                        Comparison = trigger.Comparison,
                        DedupKey = dedupKey,
                    });
                    trigger.LastFiredAt = now;
                    fires++;
                }
                else
                {
                    trigger.ArmState = TriggerArmState.Armed; // was set to Fired above; a non-delivery must re-attempt
                    _logger.LogWarning(
                        "Trigger {Id} alert was not accepted for delivery; it stays armed and re-attempts next scan.",
                        trigger.Id);
                }
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

        return fires;
    }

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

    /// <summary>The owning operator, so every per-owner read and every journaled firing stays R-20-scoped.</summary>
    private sealed record OwnerUser(Guid UserId) : ICurrentUser;
}
