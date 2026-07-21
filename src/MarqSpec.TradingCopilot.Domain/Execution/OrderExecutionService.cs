using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// The single enforcing path to a broker (R-11, ADR-0007). Guards run in a deliberate order, and an order that
/// fails either one **does not reach the venue at all** — refusal is not advisory.
/// </summary>
/// <remarks>
/// <para>
/// The size transmitted is the one the <b>gate approved</b>, never the one asked for. Sending the requested
/// quantity after a resize would make the gate advisory, which is the single thing it must never be.
/// </para>
/// <para>
/// Guards that establish the request <i>means</i> what it appears to mean run <b>before</b> the gate: an
/// authorization computed against one account or instrument says nothing about another, so evaluating an
/// incoherent request would produce a decision that does not describe what would be sent.
/// </para>
/// <para>
/// The R-14 guard is only ever as good as the <see cref="TradingMode"/> it is handed. This class enforces
/// correctly on whatever it is given; <i>how</i> that mode is established belongs to the venue layer, and a
/// venue flag alone does not establish it — see <see cref="TradingMode"/> and gh#60.
/// </para>
/// </remarks>
public sealed class OrderExecutionService : IOrderExecutionService
{
    private readonly IRiskGate _gate;
    private readonly IOrderExecutor _venue;
    private readonly DeploymentEnvironment _environment;

    /// <summary>Creates the execution service.</summary>
    /// <param name="gate">The enforcing risk gate (R-5 / R-16).</param>
    /// <param name="venue">The venue's execution slice — the only thing here that reaches a broker.</param>
    /// <param name="environment">
    /// Where this process is actually running, supplied once at composition from trusted host configuration.
    /// Deliberately <b>not</b> a per-send argument: R-14 is an enforcement boundary, and a caller able to pass
    /// <see cref="DeploymentEnvironment.Production"/> from a development host could walk a live account straight
    /// through the guard.
    /// </param>
    public OrderExecutionService(IRiskGate gate, IOrderExecutor venue, DeploymentEnvironment environment)
    {
        _gate = gate;
        _venue = venue;
        _environment = environment;
    }

    /// <inheritdoc />
    public async Task<ExecutionResult> SendAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        // R-14 first, before the order is even priced: an account this environment may not trade should be
        // refused outright rather than sized and then rejected.
        if (!TradingModePolicy.IsAllowed(request.Account.Mode, _environment))
        {
            return ExecutionResult.RefusedByMode(
                $"Account '{request.Account.Name}' is {request.Account.Mode} and may not be traded from "
                + $"{_environment} (R-14).");
        }

        if (Mismatch(request) is { } mismatch)
        {
            return ExecutionResult.RefusedByMismatch(mismatch);
        }

        if (request.Type == OrderType.TrailingStop)
        {
            // Not a venue capability question -- the neutral ticket has nowhere to put a trail distance, so no
            // venue could receive this correctly. Refused here so the answer is the same everywhere rather than
            // depending on which adapter happens to be wired in.
            return ExecutionResult.RefusedByUnsupportedType(
                "A trailing stop cannot be expressed: the ticket carries no trail distance. Stop management "
                + "arrives with staged stops (gh#11).");
        }

        GateDecision decision = _gate.Evaluate(request.Proposal, request.Risk);

        // Blocked, or resized to nothing -- the gate's "no trade". Either way nothing is transmitted.
        if (decision.Outcome == GateOutcome.Blocked || decision.ApprovedQuantity <= 0)
        {
            return ExecutionResult.RefusedByRisk(decision);
        }

        OrderRequest order = new(
            request.Account.Id,
            request.Contract.Contract,
            request.Proposal.Side,
            request.Type,
            decision.ApprovedQuantity,
            LimitPriceFor(request),
            StopPriceFor(request));

        PlacedOrder placed = await _venue.PlaceOrderAsync(order, cancellationToken);

        return ExecutionResult.Placed(placed, decision);
    }

    /// <summary>
    /// Whether the request's parts describe the same trade, and what disagrees if not. Neither pairing is
    /// structurally enforced, and both fail in the same direction: the gate authorizes one thing and the venue
    /// receives another.
    /// </summary>
    private static string? Mismatch(ExecutionRequest request)
    {
        // Equity, drawdown floor and daily limits are all account-specific. Sizing against one account and
        // transmitting to another produces a quantity the target account never justified.
        if (request.Account.Id != request.Risk.Account)
        {
            return $"The risk snapshot describes account '{request.Risk.Account}' but the order would be sent to "
                + $"'{request.Account.Id}'. Nothing was sized.";
        }

        // Tick size and point value come from the proposal's instrument; the contract is what actually gets
        // traded. An ES-sized order on an NQ contract is authorized for exposure it does not have.
        return request.Contract.Instrument != request.Proposal.Instrument.Id
            ? $"The proposal is sized for '{request.Proposal.Instrument.Id}' but the contract was resolved for "
                + $"'{request.Contract.Instrument}'. Nothing was sized."
            : null;
    }

    private static Price? LimitPriceFor(ExecutionRequest request)
    {
        return request.Type switch
        {
            OrderType.Limit or OrderType.StopLimit => request.Proposal.Entry,
            _ => null,
        };
    }

    private static Price? StopPriceFor(ExecutionRequest request)
    {
        // TrailingStop is absent deliberately -- it never reaches here, and giving it the entry as an ordinary
        // stop price is what made it look representable.
        return request.Type switch
        {
            OrderType.Stop or OrderType.StopLimit => request.Proposal.Entry,
            _ => null,
        };
    }
}
