namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// The single enforcing checkpoint every order passes through (R-5, ADR-0007). Manual tickets, taken suggestions,
/// edited takes, and conditional orders firing all funnel through here before transmission — the LLM proposes,
/// this decides, and nothing reaches a broker unchecked.
/// </summary>
public interface IRiskGate
{
    /// <summary>Sizes and authorizes — or refuses — a proposed order.</summary>
    /// <param name="proposal">The order being proposed.</param>
    /// <param name="context">Live account state, the floor, the account's rules, and the operator's tolerance.</param>
    /// <returns>The decision, carrying the approved size, the binding layer, and the reason.</returns>
    GateDecision Evaluate(OrderProposal proposal, RiskContext context);
}
