namespace MarqSpec.TradingCopilot.Integration.Tradovate;

/// <summary>
/// A fault in the Tradovate venue adapter — a Tradovate answer that cannot be mapped onto the venue-neutral model
/// (R-17), such as a contract or account the gateway returned without an id.
/// </summary>
public sealed class TradovateVenueException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TradovateVenueException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public TradovateVenueException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public TradovateVenueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
