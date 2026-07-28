using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// An operator-authored deterministic indicator trigger (gh#385, A1) — a persisted
/// <see cref="MarqSpec.TradingCopilot.Domain.Triggers.IndicatorThresholdCondition"/> plus its debounce state. A
/// periodic scan evaluates it over the pre-computed indicators and, on the arming edge, fires a mechanical alert
/// (ADR-0008: the LLM is never in the scan loop).
/// </summary>
/// <remarks>
/// <b>Operator-owned (R-20):</b> a trigger is one operator's watch, so it implements <see cref="IUserOwned"/> and
/// carries the default-deny per-user filter. The debounce state (<see cref="ArmState"/> / <see cref="ArmCycle"/>)
/// is persisted so firing survives a restart and stays idempotent across redelivery. Editing or re-enabling a
/// trigger resets <see cref="ArmState"/> to <see cref="TriggerArmState.Unseeded"/> so it re-seeds silently rather
/// than firing on a condition that was already true.
/// </remarks>
public class Trigger : IUserOwned
{
    /// <summary>The trigger's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The instrument the indicator is measured on (venue-neutral symbol, e.g. <c>ES</c>).</summary>
    public required string Instrument { get; set; }

    /// <summary>The indicator name (e.g. <c>atr</c>, <c>rsi</c>) — the projection's own identity.</summary>
    public required string Indicator { get; set; }

    /// <summary>The indicator period (e.g. Wilder 14).</summary>
    public required int Period { get; set; }

    /// <summary>The bar size, in minutes, the indicator is measured on.</summary>
    public required int ResolutionMinutes { get; set; }

    /// <summary>Which side of the threshold satisfies. Refusable zero — never <see cref="ThresholdComparison.Unknown"/>.</summary>
    public required ThresholdComparison Comparison { get; set; }

    /// <summary>The threshold value.</summary>
    public required decimal Threshold { get; set; }

    /// <summary>Where a firing goes. Refusable zero; inc-1 accepts only <see cref="TriggerRoute.MechanicalAlert"/>.</summary>
    public required TriggerRoute Route { get; set; }

    /// <summary>Whether the scan evaluates this trigger. A disabled trigger is inert but retained.</summary>
    public bool Enabled { get; set; }

    /// <summary>The debounce state. A new trigger starts <see cref="TriggerArmState.Unseeded"/> (the zero value).</summary>
    public TriggerArmState ArmState { get; set; }

    /// <summary>
    /// The firing cycle — bumped on each re-arm. Combined with <see cref="Id"/> it names the incident
    /// (<c>trigger:{Id}:{ArmCycle}</c>), so each crossing dedups and resolves as a distinct incident.
    /// </summary>
    public int ArmCycle { get; set; }

    /// <summary>When this trigger last fired, or <see langword="null"/> if never — the rate-limit / governor seam.</summary>
    public DateTimeOffset? LastFiredAt { get; set; }

    /// <summary>
    /// The rule this trigger was compiled from, once the NL → rule authoring path (R-7) lands — <see langword="null"/>
    /// for a directly-authored trigger. The only seam to the deferred <c>Rule</c> entity.
    /// </summary>
    public Guid? SourceRuleId { get; set; }

    /// <summary>When the trigger was created.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the trigger was last edited. An edit re-seeds the debounce.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}
