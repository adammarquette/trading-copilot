using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// The create / edit body for a trigger (gh#385). The same shape serves POST and PATCH — a PATCH replaces the
/// editable fields whole and re-seeds the debounce, so there is no partial-update ambiguity.
/// </summary>
/// <param name="Instrument">The instrument the indicator is measured on (venue-neutral symbol).</param>
/// <param name="Indicator">The indicator name (e.g. <c>atr</c>, <c>rsi</c>).</param>
/// <param name="Period">The indicator period.</param>
/// <param name="ResolutionMinutes">The bar size the indicator is measured on.</param>
/// <param name="Comparison">Which side of the threshold satisfies (never <see cref="ThresholdComparison.Unknown"/>).</param>
/// <param name="Threshold">The threshold value.</param>
/// <param name="Route">Where a firing goes (inc-1 accepts only <see cref="TriggerRoute.MechanicalAlert"/>).</param>
/// <param name="Enabled">Whether the scan evaluates the trigger.</param>
public sealed record TriggerRequest(
    string Instrument,
    string Indicator,
    int Period,
    int ResolutionMinutes,
    ThresholdComparison Comparison,
    decimal Threshold,
    TriggerRoute Route,
    bool Enabled);

/// <summary>The API view of a trigger (gh#385) — the persisted state including its debounce cycle.</summary>
public sealed record TriggerResponse(
    Guid Id,
    string Instrument,
    string Indicator,
    int Period,
    int ResolutionMinutes,
    ThresholdComparison Comparison,
    decimal Threshold,
    TriggerRoute Route,
    bool Enabled,
    TriggerArmState ArmState,
    int ArmCycle,
    DateTimeOffset? LastFiredAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Projects a persisted trigger into its API view.</summary>
    /// <param name="trigger">The persisted trigger.</param>
    /// <returns>The response.</returns>
    public static TriggerResponse From(Trigger trigger) => new(
        trigger.Id,
        trigger.Instrument,
        trigger.Indicator,
        trigger.Period,
        trigger.ResolutionMinutes,
        trigger.Comparison,
        trigger.Threshold,
        trigger.Route,
        trigger.Enabled,
        trigger.ArmState,
        trigger.ArmCycle,
        trigger.LastFiredAt,
        trigger.CreatedAt,
        trigger.UpdatedAt);
}
