namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>What happened to an order on its way to the broker.</summary>
public enum ExecutionOutcome
{
    /// <summary>The venue accepted it — at the size the gate approved, which may be smaller than asked for.</summary>
    Placed,

    /// <summary>
    /// Refused by R-14's <b>environment restriction</b>: this account's mode may not be traded from this
    /// deployment environment — or, for <see cref="Venue.TradingMode.Undeclared"/>, from any environment at all,
    /// because nothing has established whether capital is at risk. Nothing was sized and nothing was sent.
    /// </summary>
    /// <remarks>
    /// Not to be confused with R-14's <i>mode guard</i> (PRD R-14), which forbids <b>persisting</b> an
    /// <c>Order</c> or <c>Suggestion</c> whose mode conflicts with its parent account's. That is a journal
    /// integrity rule enforced at the repository layer and by a DB check constraint; it is not this guard, and
    /// it has nothing to act on until orders are persisted.
    /// </remarks>
    RefusedByMode,

    /// <summary>
    /// Refused by the risk gate (R-5 / R-16) — blocked outright, or resized to nothing, which is the gate's
    /// "no trade". Nothing was sent.
    /// </summary>
    RefusedByRisk,

    /// <summary>
    /// Refused because the venue itself reports the account as not tradable. Distinct from
    /// <see cref="RefusedByMode"/>, which is about this <i>environment</i>: here the account is ineligible
    /// everywhere, and letting the broker reject the ticket would mean it left the enforcing path first.
    /// </summary>
    RefusedByAccountState,

    /// <summary>
    /// Refused because the request contradicts itself: the risk snapshot describes a different account than the
    /// one being traded, the contract was resolved for a different instrument than the proposal is sized for, or
    /// the account and contract belong to a different venue than the executor. Refused <b>before</b> the gate
    /// runs — evaluating an incoherent request would produce an authorization for something other than what
    /// would be sent.
    /// </summary>
    RefusedByMismatch,

    /// <summary>
    /// Refused because the ticket cannot express this order type. Distinct from a venue lacking the capability:
    /// the request itself is unrepresentable, so no venue could receive it correctly.
    /// </summary>
    RefusedByUnsupportedType,

    /// <summary>
    /// The full ladder ran and the gate decided — but nothing was transmitted (the arm/edit path, gh#11).
    /// The decision may be allowing, resizing, or blocking; transmission is a separate, explicit act.
    /// </summary>
    Evaluated,
}
