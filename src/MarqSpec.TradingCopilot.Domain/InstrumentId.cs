namespace MarqSpec.TradingCopilot.Domain;

/// <summary>
/// A venue-neutral instrument identifier (for example <c>ES</c>, <c>NQ</c>, <c>CL</c>). Parsed at the boundary
/// (parse, don't validate) so a blank or malformed symbol can never flow into risk, execution, or the journal.
/// </summary>
public readonly record struct InstrumentId
{
    private InstrumentId(string symbol)
    {
        Symbol = symbol;
    }

    /// <summary>The normalized (trimmed, upper-cased) instrument symbol.</summary>
    public string Symbol { get; }

    /// <summary>Parses <paramref name="value"/> into an <see cref="InstrumentId"/>.</summary>
    /// <param name="value">The candidate symbol.</param>
    /// <returns>The parsed identifier.</returns>
    /// <exception cref="FormatException"><paramref name="value"/> is null, empty, or whitespace.</exception>
    public static InstrumentId Parse(string? value)
    {
        return TryParse(value, out InstrumentId id)
            ? id
            : throw new FormatException($"'{value}' is not a valid instrument symbol.");
    }

    /// <summary>Attempts to parse <paramref name="value"/> without throwing.</summary>
    /// <param name="value">The candidate symbol.</param>
    /// <param name="id">The parsed identifier when this returns <see langword="true"/>; otherwise the default.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/> was a non-blank symbol.</returns>
    public static bool TryParse(string? value, out InstrumentId id)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            id = default;
            return false;
        }

        id = new InstrumentId(value.Trim().ToUpperInvariant());
        return true;
    }

    /// <summary>Returns the <see cref="Symbol"/>.</summary>
    /// <returns>The instrument symbol, or an empty string for the default value.</returns>
    public override string ToString()
    {
        return Symbol ?? string.Empty;
    }
}
