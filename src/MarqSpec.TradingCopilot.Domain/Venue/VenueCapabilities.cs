namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The set of <see cref="VenueCapability"/> flags a venue grants — its half of the R-17 capability matrix. Each
/// adapter declares its own; callers ask before relying on one.
/// </summary>
public readonly record struct VenueCapabilities
{
    private VenueCapabilities(VenueCapability flags)
    {
        Flags = flags;
    }

    /// <summary>A venue that grants nothing.</summary>
    public static VenueCapabilities None => new(VenueCapability.None);

    /// <summary>The granted capability flags.</summary>
    public VenueCapability Flags { get; }

    /// <summary>Creates a capability set from the granted <paramref name="capabilities"/>.</summary>
    /// <param name="capabilities">The flags the venue grants.</param>
    /// <returns>The capability set.</returns>
    public static VenueCapabilities Of(VenueCapability capabilities)
    {
        return new VenueCapabilities(capabilities);
    }

    /// <summary>Indicates whether every flag in <paramref name="capability"/> is granted.</summary>
    /// <param name="capability">The capability — or composite of capabilities — to check.</param>
    /// <returns><see langword="true"/> if all requested flags are granted.</returns>
    public bool Supports(VenueCapability capability)
    {
        return (Flags & capability) == capability;
    }

    /// <summary>Throws unless every flag in <paramref name="capability"/> is granted.</summary>
    /// <param name="capability">The capability the caller is about to rely on.</param>
    /// <exception cref="VenueCapabilityNotSupportedException">The capability is not granted.</exception>
    public void Require(VenueCapability capability)
    {
        if (!Supports(capability))
        {
            throw new VenueCapabilityNotSupportedException(capability);
        }
    }
}
