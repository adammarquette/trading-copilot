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
/// <b>Known limitation.</b> The R-14 guard is only as good as the <see cref="TradingMode"/> it is handed. That
/// mode currently comes from the venue's own flag, which describes <i>where an order executes</i> rather than
/// <i>what is at stake</i> — so a prop-firm funded account reads as practice and would pass this guard outside
/// production. This class enforces correctly on whatever it is given; the derivation is wrong upstream and is
/// tracked in gh#60. Nothing here needs to change when that is fixed.
/// </para>
/// </remarks>
public sealed class OrderExecutionService : IOrderExecutionService
{
    private readonly IRiskGate _gate;
    private readonly IOrderExecutor _venue;

    /// <summary>Creates the execution service.</summary>
    /// <param name="gate">The enforcing risk gate (R-5 / R-16).</param>
    /// <param name="venue">The venue's execution slice — the only thing here that reaches a broker.</param>
    public OrderExecutionService(IRiskGate gate, IOrderExecutor venue)
    {
        _gate = gate;
        _venue = venue;
    }

    /// <inheritdoc />
    public async Task<ExecutionResult> SendAsync(
        ExecutionRequest request,
        DeploymentEnvironment environment,
        CancellationToken cancellationToken)
    {
        // R-14 first, before the order is even priced: an account this environment may not trade should be
        // refused outright rather than sized and then rejected.
        if (!TradingModePolicy.IsAllowed(request.Account.Mode, environment))
        {
            return ExecutionResult.RefusedByMode(
                $"Account '{request.Account.Name}' is {request.Account.Mode} and may not be traded from "
                + $"{environment} — practice accounts only outside production (R-14).");
        }

        GateDecision decision = _gate.Evaluate(request.Proposal, request.Risk);

        // Blocked, or resized to nothing -- the gate's "no trade". Either way nothing is transmitted.
        if (decision.Outcome == GateOutcome.Blocked || decision.ApprovedQuantity <= 0)
        {
            return ExecutionResult.RefusedByRisk(decision);
        }

        OrderRequest order = new(
            request.Account.Id,
            request.Contract,
            request.Proposal.Side,
            request.Type,
            decision.ApprovedQuantity,
            LimitPriceFor(request),
            StopPriceFor(request));

        PlacedOrder placed = await _venue.PlaceOrderAsync(order, cancellationToken);

        return ExecutionResult.Placed(placed, decision);
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
        return request.Type switch
        {
            OrderType.Stop or OrderType.StopLimit or OrderType.TrailingStop => request.Proposal.Entry,
            _ => null,
        };
    }
}
