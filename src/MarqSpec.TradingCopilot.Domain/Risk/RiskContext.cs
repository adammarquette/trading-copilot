namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// Everything the gate needs to decide: live account state, where the floor sits, the account's hard rules, and
/// the operator's own tolerance and caps. Assembled per evaluation, so a decision is always made against the
/// account as it stands right now — never a cached view (R-5).
/// </summary>
/// <param name="Account">
/// The account this context describes. Carried so the execution path can prove the order is transmitted to the
/// <i>same</i> account the gate sized against — equity, drawdown floor and daily limits are all account-specific,
/// so evaluating against one account and sending to another would authorize a size the target account never
/// justified.
/// </param>
/// <param name="State">Live account state from the venue.</param>
/// <param name="Drawdown">The account's trailing drawdown floor.</param>
/// <param name="Rules">The account's hard rules.</param>
/// <param name="Profile">The operator's configured risk tolerance.</param>
/// <param name="Manual">The operator's manual contract caps.</param>
/// <param name="Sanity">The execution sanity caps (R-16).</param>
public sealed record RiskContext(
    Venue.VenueAccountId Account,
    AccountRiskState State,
    TrailingDrawdown Drawdown,
    AccountRiskRules Rules,
    RiskProfile Profile,
    ManualCaps Manual,
    SanityCaps Sanity);
