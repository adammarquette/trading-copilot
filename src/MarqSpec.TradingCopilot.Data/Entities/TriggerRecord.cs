using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A standing, deterministic trigger (gh#385, R-4 / R-7, ADR-0008): "&lt;indicator&gt;(&lt;period&gt;) on
/// &lt;resolution&gt;m is &lt;below|above&gt; &lt;threshold&gt; on &lt;symbol&gt;". The persisted authoring surface
/// the scan reads; the pure evaluation is <see cref="ToCondition"/> + <see cref="TriggerDebounce"/>, so this row is
/// data only.
/// </summary>
/// <remarks>
/// Operator-owned (R-20). The debounce memory — <see cref="ArmState"/> and <see cref="ArmCycle"/> — turns the level
/// condition into an edge-triggered, fire-once alert: a crossing fires exactly one incident, and a re-arm bumps the
/// cycle so the <i>next</i> crossing mints a fresh dedup key. Enum fields whose zero is refused are <c>required</c>
/// and DB-check-constrained (defense-in-depth below the boundary). <see cref="SourceRuleId"/> is the only R-7 seam:
/// a soft reference (no FK, no navigation) to the rulebook rule that authored the trigger, when one did.
/// </remarks>
public class TriggerRecord : IUserOwned
{
    /// <summary>The record's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20).</summary>
    public Guid UserId { get; set; }

    /// <summary>The venue-neutral instrument symbol (e.g. <c>ES</c>) — the <c>IIndicatorSource</c> read identity.</summary>
    public required string Symbol { get; set; }

    /// <summary>The R-22 stored indicator name (e.g. <c>rsi</c>, <c>atr</c>).</summary>
    public required string Indicator { get; set; }

    /// <summary>The indicator period — part of the value's identity, not a detail.</summary>
    public required int Period { get; set; }

    /// <summary>The bar size, in minutes, the indicator was computed over.</summary>
    public required int ResolutionMinutes { get; set; }

    /// <summary>Which kind of condition this is. <c>required</c>; a DB check refuses unknown.</summary>
    public required TriggerConditionKind ConditionKind { get; set; }

    /// <summary>Whether the value must be below or above the threshold. <c>required</c>; a DB check refuses unknown.</summary>
    public required IndicatorComparison Comparison { get; set; }

    /// <summary>The threshold the indicator is compared against.</summary>
    public required decimal Threshold { get; set; }

    /// <summary>The optional hysteresis dead-band that widens the re-arm boundary. NULL (or non-positive) means inert.</summary>
    public decimal? Hysteresis { get; set; }

    /// <summary>Where a fire routes. <c>required</c>; a DB check refuses unknown, and the evaluator refuses AgentReview.</summary>
    public required TriggerRoute Route { get; set; }

    /// <summary>How loudly the mechanical alert arrives when it fires.</summary>
    public required NotificationSeverity Severity { get; set; }

    /// <summary>Whether the scan evaluates this trigger. A disabled trigger is neither read nor fired.</summary>
    public required bool Enabled { get; set; }

    /// <summary>The debounce state. <c>required</c>; the fail-closed zero (<see cref="TriggerArmState.Unseeded"/>) re-seeds silently.</summary>
    public required TriggerArmState ArmState { get; set; }

    /// <summary>The incident counter — bumped on re-arm so each crossing is a distinct dedup key even if a resolve is missed.</summary>
    public required int ArmCycle { get; set; }

    /// <summary>The last value the scan measured, or NULL when the last read could not be measured.</summary>
    public decimal? LastEvaluatedValue { get; set; }

    /// <summary>When the trigger last fired, or NULL if it never has.</summary>
    public DateTimeOffset? LastFiredAt { get; set; }

    /// <summary>
    /// When this trigger's current run of <b>unevaluable</b> readings began, or <see langword="null"/> when the
    /// last reading was measurable (gh#469).
    /// </summary>
    /// <remarks>
    /// The fail-closed hold on an unmeasurable indicator was always correct; what it could not do is tell the
    /// difference between a late bar and a dependency that stopped being produced. This is that difference:
    /// <see cref="TriggerStaleness"/> reads it to decide when an outage has lasted long enough to report. It is
    /// state rather than a per-pass log because the distinguishing fact is <b>duration</b>.
    /// </remarks>
    public DateTimeOffset? UnmeasurableSince { get; set; }

    /// <summary>
    /// The rulebook rule (R-7) that authored this trigger, when one did — a <b>soft</b> reference (no FK, no
    /// navigation), so the trigger outlives the rule and the trigger layer stays decoupled from the rulebook.
    /// </summary>
    public Guid? SourceRuleId { get; set; }

    /// <summary>
    /// The account a fired <see cref="TriggerRoute.AgentReview"/> suggestion is issued against (R-14) — a real FK,
    /// set <b>only</b> on the agent-review route and NULL for a mechanical trigger (a DB check pairs it with
    /// <see cref="Route"/>). The suggestion reads the account's <b>live</b> mode at issuance; nothing about mode is
    /// stored here.
    /// </summary>
    public Guid? AccountId { get; set; }

    /// <summary>
    /// The contract size a fired <see cref="TriggerRoute.AgentReview"/> suggestion is issued at (R-16) — set
    /// <b>only</b> on the agent-review route and always positive there, NULL for a mechanical trigger. Size is the
    /// <b>operator's</b>, fixed on the trigger; the model never chooses it.
    /// </summary>
    public int? Size { get; set; }

    /// <summary>When the trigger was created.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>Projects the persisted fields into the pure domain condition the scan evaluates.</summary>
    /// <returns>The indicator-threshold condition over this trigger's read identity.</returns>
    public IndicatorThresholdCondition ToCondition() =>
        new(InstrumentId.Parse(Symbol), Indicator, Period, ResolutionMinutes, Comparison, Threshold, Hysteresis);
}
