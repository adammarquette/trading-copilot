namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A venue-qualified trading-account identifier — the venue plus that venue's own account handle. Two venues can
/// each hand out account <c>9001</c>, so accounts are never identified by the bare handle (R-17).
/// </summary>
public readonly record struct VenueAccountId
{
    private const char Separator = ':';

    private VenueAccountId(VenueId venue, string key)
    {
        Venue = venue;
        Key = key;
    }

    /// <summary>The venue the account belongs to.</summary>
    public VenueId Venue { get; }

    /// <summary>
    /// The venue's own account handle. Opaque and case-preserved — normalizing case could break a real identifier.
    /// </summary>
    public string Key { get; }

    /// <summary>Creates a venue-qualified account identifier.</summary>
    /// <param name="venue">The venue the account belongs to; must not be the default.</param>
    /// <param name="key">The venue's account handle; must not be blank.</param>
    /// <returns>The qualified identifier.</returns>
    /// <exception cref="ArgumentException"><paramref name="venue"/> is the default or <paramref name="key"/> is blank.</exception>
    public static VenueAccountId Create(VenueId venue, string key)
    {
        if (string.IsNullOrEmpty(venue.Value))
        {
            throw new ArgumentException("A venue account must be tagged with a venue.", nameof(venue));
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A venue account key must not be blank.", nameof(key));
        }

        return new VenueAccountId(venue, key.Trim());
    }

    /// <summary>Attempts to parse the qualified <c>venue:key</c> form produced by <see cref="ToString"/>.</summary>
    /// <param name="value">The candidate qualified identifier.</param>
    /// <param name="id">The parsed identifier when this returns <see langword="true"/>; otherwise the default.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/> was a well-formed qualified identifier.</returns>
    public static bool TryParse(string? value, out VenueAccountId id)
    {
        id = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int separator = value.IndexOf(Separator);
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        if (!VenueId.TryParse(value[..separator], out VenueId venue))
        {
            return false;
        }

        string key = value[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        id = new VenueAccountId(venue, key.Trim());
        return true;
    }

    /// <summary>Returns the qualified <c>venue:key</c> form.</summary>
    /// <returns>The qualified identifier, or an empty string for the default value.</returns>
    public override string ToString()
    {
        return Key is null ? string.Empty : $"{Venue}{Separator}{Key}";
    }
}
