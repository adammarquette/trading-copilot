namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// Identifies a venue — a trading platform (<c>projectx</c>, <c>tradovate</c>) or a data-only provider
/// (<c>finnhub</c>). Every instrument, account, order, and fill that crosses the seam carries one (R-17), so
/// risk, P&amp;L, and the journal stay correct when accounts span venues.
/// </summary>
public readonly record struct VenueId
{
    private VenueId(string value)
    {
        Value = value;
    }

    /// <summary>The normalized (trimmed, lower-cased) venue key.</summary>
    public string Value { get; }

    /// <summary>Parses <paramref name="value"/> into a <see cref="VenueId"/>.</summary>
    /// <param name="value">The candidate venue key.</param>
    /// <returns>The parsed identifier.</returns>
    /// <exception cref="FormatException"><paramref name="value"/> is null, empty, or whitespace.</exception>
    public static VenueId Parse(string? value)
    {
        return TryParse(value, out VenueId id)
            ? id
            : throw new FormatException($"'{value}' is not a valid venue key.");
    }

    /// <summary>Attempts to parse <paramref name="value"/> without throwing.</summary>
    /// <param name="value">The candidate venue key.</param>
    /// <param name="id">The parsed identifier when this returns <see langword="true"/>; otherwise the default.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/> was a non-blank key.</returns>
    public static bool TryParse(string? value, out VenueId id)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            id = default;
            return false;
        }

        id = new VenueId(value.Trim().ToLowerInvariant());
        return true;
    }

    /// <summary>Returns the <see cref="Value"/>.</summary>
    /// <returns>The venue key, or an empty string for the default value.</returns>
    public override string ToString()
    {
        return Value ?? string.Empty;
    }
}
