namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// What the gate decided, and why. The <see cref="BindingLayer"/> and <see cref="Reason"/> are not decoration —
/// R-5 requires every suggestion and order to show its computed size and which layer bound it, so a trader is
/// never told "no" without being told why.
/// </summary>
/// <param name="Outcome">Allowed, resized, or blocked.</param>
/// <param name="ApprovedQuantity">The contracts the gate authorizes. Zero means no trade.</param>
/// <param name="BindingLayer">The layer that bound the decision, or <see langword="null"/> if none did.</param>
/// <param name="Reason">A human-readable explanation, always populated.</param>
public sealed record GateDecision(
    GateOutcome Outcome,
    int ApprovedQuantity,
    RiskLayer? BindingLayer,
    string Reason)
{
    /// <summary>The order passes at the size asked for.</summary>
    /// <param name="quantity">The approved quantity.</param>
    /// <param name="reason">Why it passed.</param>
    /// <returns>An allowing decision.</returns>
    public static GateDecision Allow(int quantity, string reason)
    {
        return new GateDecision(GateOutcome.Allowed, quantity, BindingLayer: null, reason);
    }

    /// <summary>The order passes smaller, bound by a layer.</summary>
    /// <param name="quantity">The reduced quantity.</param>
    /// <param name="bindingLayer">The layer that bound it.</param>
    /// <param name="reason">Why it was reduced.</param>
    /// <returns>A resizing decision.</returns>
    public static GateDecision Resize(int quantity, RiskLayer bindingLayer, string reason)
    {
        return new GateDecision(GateOutcome.Resized, quantity, bindingLayer, reason);
    }

    /// <summary>The order does not go.</summary>
    /// <param name="bindingLayer">The layer that refused it.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <returns>A blocking decision with a zero quantity.</returns>
    public static GateDecision Block(RiskLayer bindingLayer, string reason)
    {
        return new GateDecision(GateOutcome.Blocked, ApprovedQuantity: 0, bindingLayer, reason);
    }
}
