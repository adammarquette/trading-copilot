namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// The condition that fires a conditional order (ADR-0007, gh#176): a trigger price and the direction price must
/// cross it. Deterministic and side-explicit — the same "the domain decides, from exactly what is stored"
/// pattern as <see cref="StopPlan.ShouldPromote"/>.
/// </summary>
/// <param name="TriggerPrice">The price at which the order fires.</param>
/// <param name="Direction">Which way price must cross <paramref name="TriggerPrice"/> to fire.</param>
public sealed record ConditionalTrigger(Price TriggerPrice, ConditionalCrossDirection Direction)
{
    /// <summary>
    /// Whether the current market price has crossed the trigger the declared way. An
    /// <see cref="ConditionalCrossDirection.Unknown"/> direction never fires — fail-closed, so an unset or
    /// later-added direction rests rather than firing on the wrong side.
    /// </summary>
    /// <param name="current">The current market price.</param>
    /// <returns><see langword="true"/> when the trigger has fired.</returns>
    public bool HasFired(Price current)
    {
        return Direction switch
        {
            ConditionalCrossDirection.RisesTo => current.Value >= TriggerPrice.Value,
            ConditionalCrossDirection.FallsTo => current.Value <= TriggerPrice.Value,
            _ => false,
        };
    }
}
