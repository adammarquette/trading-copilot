using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// The single path an order takes to a broker. Manual tickets, taken suggestions, and edited takes all funnel
/// through here so the guards cannot be routed around (ADR-0007).
/// </summary>
public interface IOrderExecutionService
{
    /// <summary>Puts one order through the guards and, if it survives, on the wire.</summary>
    /// <param name="request">The order, the account, and the risk context to judge it against.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened, with the gate's decision and a reason.</returns>
    /// <remarks>
    /// The deployment environment the R-14 guard reads is fixed at construction rather than passed here: a
    /// caller able to name its own environment could walk a live account through the guard from a development
    /// host, which would make the boundary advisory.
    /// </remarks>
    Task<ExecutionResult> SendAsync(ExecutionRequest request, CancellationToken cancellationToken);
}
