using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// Everything needed to put one order through the gate and, if it survives, on the wire. Assembled per send, so
/// a decision is always made against the account and market as they stand now.
/// </summary>
/// <remarks>
/// The four parts must agree, and nothing structural forces them to: the account traded must be the account the
/// risk snapshot describes, and the contract transmitted must be the one resolved for the instrument the proposal
/// is sized against. <see cref="OrderExecutionService"/> refuses a request where either disagrees, before the
/// gate runs — an authorization computed from one account or instrument says nothing about another.
/// </remarks>
/// <param name="Proposal">The risk shape — instrument, side, size asked for, entry, working stop, safety stop.</param>
/// <param name="Contract">The venue's contract handle, paired with the instrument it was resolved for.</param>
/// <param name="Account">The account to trade, carrying the <see cref="TradingMode"/> the R-14 guard reads.</param>
/// <param name="Risk">Live account state and the layered limits the gate sizes against.</param>
/// <param name="Type">
/// How the order rests. The proposal describes what is <i>at risk</i>; this says how it is <i>worked</i>, and the
/// two are separate concerns — the same risk shape can be a market fill or a resting limit.
/// <see cref="OrderType.TrailingStop"/> is <b>not</b> accepted: the ticket carries no trail distance, so the
/// order cannot be expressed (gh#11).
/// </param>
public sealed record ExecutionRequest(
    OrderProposal Proposal,
    ResolvedContract Contract,
    VenueAccount Account,
    RiskContext Risk,
    OrderType Type = OrderType.Market);
