namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A <b>venue-neutral</b> refusal of an order operation (gh#629), carrying the <see cref="Kind"/> a catch site needs
/// to tell a <b>definitive rejection</b> (nothing rests — safe to auto-resolve) from an <b>indeterminate</b> fault
/// (the order may be live — leave for a human). Thrown by the venue adapter at the seam
/// (<see cref="ITradingVenue"/>) and consumed in <c>Api</c> without either side depending on a venue-specific
/// exception type — a second venue reuses it unchanged.
/// </summary>
/// <remarks>
/// Classification happens <b>where <c>!success</c> is known</b> — the adapter's throw site — never at the catch by
/// inspecting a message or an error code, which would be fragile. The parameterless-kind constructor defaults to
/// <see cref="VenueRefusalKind.Indeterminate"/> so an un-classified refusal fails safe (the order is assumed
/// maybe-live), matching the enum's zero value.
/// </remarks>
public sealed class VenueRefusalException : Exception
{
    /// <summary>Creates an <see cref="VenueRefusalKind.Indeterminate"/> refusal — the fail-safe default.</summary>
    /// <param name="message">What the venue refused, and why.</param>
    public VenueRefusalException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a refusal of the given kind.</summary>
    /// <param name="message">What the venue refused, and why.</param>
    /// <param name="kind">Whether the rejection is definitive (nothing rests) or the order's fate is indeterminate.</param>
    /// <param name="errorCode">The gateway's error code, where it supplied one.</param>
    public VenueRefusalException(string message, VenueRefusalKind kind, int? errorCode = null)
        : base(message)
    {
        Kind = kind;
        ErrorCode = errorCode;
    }

    /// <summary>Whether the order was definitively rejected or its fate is indeterminate. Defaults to indeterminate.</summary>
    public VenueRefusalKind Kind { get; }

    /// <summary>The gateway's error code, where it supplied one.</summary>
    public int? ErrorCode { get; }
}
