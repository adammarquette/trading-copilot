namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A venue-qualified contract handle — what a venue calls the thing an <see cref="InstrumentId"/> names (ProjectX
/// <c>CON.F.US.EP.M25</c> for <c>ES</c>). The mapping is the adapter's job; the core passes the handle around
/// venue-tagged so an <c>ESM25</c> on one venue is never mistaken for another's (R-17).
/// </summary>
public readonly record struct VenueContractId
{
    private const char Separator = ':';

    private VenueContractId(VenueId venue, string key)
    {
        Venue = venue;
        Key = key;
    }

    /// <summary>The venue the contract handle belongs to.</summary>
    public VenueId Venue { get; }

    /// <summary>The venue's own contract handle. Opaque and case-preserved.</summary>
    public string Key { get; }

    /// <summary>Creates a venue-qualified contract handle.</summary>
    /// <param name="venue">The venue the handle belongs to; must not be the default.</param>
    /// <param name="key">The venue's contract handle; must not be blank.</param>
    /// <returns>The qualified handle.</returns>
    /// <exception cref="ArgumentException"><paramref name="venue"/> is the default or <paramref name="key"/> is blank.</exception>
    public static VenueContractId Create(VenueId venue, string key)
    {
        if (string.IsNullOrEmpty(venue.Value))
        {
            throw new ArgumentException("A venue contract must be tagged with a venue.", nameof(venue));
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A venue contract key must not be blank.", nameof(key));
        }

        return new VenueContractId(venue, key.Trim());
    }

    /// <summary>Returns the qualified <c>venue:key</c> form.</summary>
    /// <returns>The qualified handle, or an empty string for the default value.</returns>
    public override string ToString()
    {
        return Key is null ? string.Empty : $"{Venue}{Separator}{Key}";
    }
}
