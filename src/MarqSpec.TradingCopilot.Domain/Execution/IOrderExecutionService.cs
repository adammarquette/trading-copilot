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
    /// <param name="environment">The environment this deployment is running in, for the R-14 mode guard.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened, with the gate's decision and a reason.</returns>
    Task<ExecutionResult> SendAsync(
        ExecutionRequest request,
        DeploymentEnvironment environment,
        CancellationToken cancellationToken);
}
