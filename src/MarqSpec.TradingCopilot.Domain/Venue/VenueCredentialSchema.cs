namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The ordered set of <see cref="VenueCredentialField"/>s a venue's setup contract declares (ADR-0023, gh#64) — the
/// half of the contract that fixes the "2-vs-7 fields" problem: ProjectX declares 2, Tradovate will declare 7, and the
/// onboarding form is generated from whichever the adapter declares rather than hand-written per venue.
/// </summary>
/// <remarks>
/// Order is meaningful — it is the order the onboarding form renders the fields — so construction preserves the
/// declared order exactly. Keys are unique <em>case-insensitively</em>, because .NET configuration keys are, so two
/// fields differing only in case would collide on one config entry; that ambiguity is refused at declaration.
/// </remarks>
public sealed class VenueCredentialSchema
{
    private VenueCredentialSchema(IReadOnlyList<VenueCredentialField> fields)
    {
        Fields = fields;
    }

    /// <summary>The declared fields, in the order the onboarding form renders them.</summary>
    public IReadOnlyList<VenueCredentialField> Fields { get; }

    /// <summary>Declares a credential schema from an ordered, non-empty set of fields with case-insensitively unique keys.</summary>
    /// <param name="fields">The fields, in render order; at least one, no duplicate keys.</param>
    /// <returns>The schema, holding a defensive copy so a later mutation of <paramref name="fields"/> cannot alter it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/>, or any element, is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fields"/> is empty, or two fields share a key (ignoring case).</exception>
    public static VenueCredentialSchema Of(params VenueCredentialField[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Length == 0)
        {
            throw new ArgumentException("A venue credential schema declares at least one field.", nameof(fields));
        }

        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (VenueCredentialField field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            if (!keys.Add(field.Key))
            {
                throw new ArgumentException(
                    $"Duplicate venue credential key '{field.Key}' (keys are case-insensitive).", nameof(fields));
            }
        }

        // A defensive copy: the caller keeps its array, but the schema's field order and contents are now immutable.
        return new VenueCredentialSchema([.. fields]);
    }
}
