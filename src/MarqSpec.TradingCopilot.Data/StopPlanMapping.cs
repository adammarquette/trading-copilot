using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// Bridges the persisted staged-stop plan to the domain vocabulary (gh#11) — the
/// <c>Firm.ToConventions()</c> pattern: the promotion decision is made from exactly what is stored.
/// </summary>
public static class StopPlanMapping
{
    /// <summary>
    /// Rebuilds the domain plan from the record plus its parent order, which carries the instrument.
    /// </summary>
    /// <param name="record">The persisted plan.</param>
    /// <param name="order">The order it protects — supplies symbol, tick size, and point value.</param>
    /// <returns>The domain plan, at its persisted staging.</returns>
    public static StopPlan ToStopPlan(this StopPlanRecord record, Order order)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(order);

        StopProximity proximity = record.ProximityMetric switch
        {
            StopProximityMetric.Ticks => StopProximity.Ticks((int)record.ProximityValue),
            StopProximityMetric.DistanceFraction => StopProximity.DistanceFraction(record.ProximityValue),
            StopProximityMetric.AverageTrueRange => StopProximity.AverageTrueRange(record.ProximityValue),

            // Whitelist: an unrecognized metric is refused, never silently treated as ticks.
            _ => throw new InvalidOperationException(
                $"Stop plan {record.Id} carries an unrecognized proximity metric '{record.ProximityMetric}'."),
        };

        StopPlan plan = StopPlan.Create(
            InstrumentSpec.Create(
                InstrumentId.Parse(order.Symbol ?? order.Instrument), order.TickSize, order.PointValue),
            record.Side,
            new Price(record.EntryPrice),
            new Price(record.ActualStopPrice),
            new Price(record.SafetyStopPrice),
            proximity);

        // Create always starts Hidden; restore the persisted staging so a promoted stop is not re-promoted.
        return record.Staging == StopStaging.Native ? plan.Promote() : plan;
    }
}
