namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// Thrown when a <see cref="TradingMode"/> is not permitted in the current
/// <see cref="DeploymentEnvironment"/> — in practice, a live real-money account reached from anywhere but
/// production (R-14).
/// </summary>
public sealed class TradingModeNotAllowedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the mode was refused.</param>
    public TradingModeNotAllowedException(string message)
        : base(message)
    {
    }
}
