using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// Bridges the persisted conditional order to the domain aggregate (gh#176) — the <c>Firm.ToConventions()</c>
/// pattern: the fire/cancel decision is made from exactly what is stored.
/// </summary>
public static class ConditionalOrderMapping
{
    /// <summary>Rebuilds the domain <see cref="ConditionalOrder"/> at its persisted status.</summary>
    /// <param name="record">The persisted conditional order.</param>
    /// <returns>The domain aggregate, restored to its stored lifecycle state.</returns>
    public static ConditionalOrder ToConditionalOrder(this ConditionalOrderRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        OrderProposal proposal = new(
            InstrumentSpec.Create(
                InstrumentId.Parse(record.Symbol ?? record.Instrument), record.TickSize, record.PointValue),
            record.Side,
            record.Size,
            new Price(record.EntryPrice),
            new Price(record.WorkingStopPrice),
            new Price(record.SafetyStopPrice),
            new Price(record.ReferencePrice),
            record.TakeProfitPrice is { } takeProfit ? new Price(takeProfit) : null);

        ConditionalTrigger trigger = new(new Price(record.TriggerPrice), record.TriggerDirection);

        ConditionalOrder order = ConditionalOrder.Create(
            proposal,
            trigger,
            record.CancelDriftPrice is { } drift ? new Price(drift) : null,
            record.ExpiresAt);

        // Create always starts Pending; restore the persisted status so a resolved order is never re-fired.
        return record.Status switch
        {
            ConditionalStatus.Fired => order.Fire(),
            ConditionalStatus.Cancelled => order.Cancel(),
            ConditionalStatus.Expired => order.Expire(),
            ConditionalStatus.Pending => order,

            // Mid-fire (gh#577): a durable pre-transmit intent restored inert — ShouldFire / ShouldCancel are both
            // false in this state, so rebuilding a Firing record never re-fires it from a stale quote.
            ConditionalStatus.Firing => order.BeginFiring(),

            // Whitelist: an unrecognized status is refused, never silently treated as pending.
            _ => throw new InvalidOperationException(
                $"Conditional order {record.Id} carries an unrecognized status '{record.Status}'."),
        };
    }
}
