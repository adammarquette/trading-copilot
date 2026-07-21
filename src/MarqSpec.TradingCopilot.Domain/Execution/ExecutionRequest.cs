using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// Everything needed to put one order through the gate and, if it survives, on the wire. Assembled per send, so
/// a decision is always made against the account and market as they stand now.
/// </summary>
/// <param name="Proposal">The risk shape — instrument, side, size asked for, entry, working stop, safety stop.</param>
/// <param name="Contract">The venue's contract handle, already resolved for the instrument.</param>
/// <param name="Account">The account to trade, carrying the <see cref="TradingMode"/> the R-14 guard reads.</param>
/// <param name="Risk">Live account state and the layered limits the gate sizes against.</param>
/// <param name="Type">
/// How the order rests. The proposal describes what is <i>at risk</i>; this says how it is <i>worked</i>, and the
/// two are separate concerns — the same risk shape can be a market fill or a resting limit.
/// </param>
public sealed record ExecutionRequest(
    OrderProposal Proposal,
    VenueContractId Contract,
    VenueAccount Account,
    RiskContext Risk,
    OrderType Type = OrderType.Market);
