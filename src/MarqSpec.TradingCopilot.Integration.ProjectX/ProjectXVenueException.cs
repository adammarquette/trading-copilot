namespace MarqSpec.TradingCopilot.Integration.ProjectX;

/// <summary>
/// Thrown when the ProjectX gateway refuses an operation. The gateway reports most failures <b>in the payload</b>
/// (<c>success = false</c> on an otherwise healthy response) rather than by status code, so the adapter turns
/// those into exceptions — a rejected order must never look like a placed one.
/// </summary>
public sealed class ProjectXVenueException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What the gateway refused, and why.</param>
    public ProjectXVenueException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the gateway's error code.</summary>
    /// <param name="message">What the gateway refused, and why.</param>
    /// <param name="errorCode">The gateway's error code.</param>
    public ProjectXVenueException(string message, int errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The gateway's error code, where it supplied one.</summary>
    public int? ErrorCode { get; }
}
