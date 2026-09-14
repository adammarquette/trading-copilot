namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// Thrown when a caller requires a <see cref="VenueCapability"/> the venue does not grant — the explicit failure
/// R-17 asks for, in place of a silent gap discovered mid-execution.
/// </summary>
public sealed class VenueCapabilityNotSupportedException : NotSupportedException
{
    /// <summary>Creates the exception for the capability that was missing.</summary>
    /// <param name="missingCapability">The capability that was required but not granted.</param>
    public VenueCapabilityNotSupportedException(VenueCapability missingCapability)
        : base($"The venue does not support the '{missingCapability}' capability.")
    {
        MissingCapability = missingCapability;
    }

    /// <summary>The capability that was required but not granted.</summary>
    public VenueCapability MissingCapability { get; }
}
