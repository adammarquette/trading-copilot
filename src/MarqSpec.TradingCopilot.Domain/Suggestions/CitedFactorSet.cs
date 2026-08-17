namespace MarqSpec.TradingCopilot.Domain.Suggestions;

/// <summary>
/// The pure primary-selection rule over a suggestion's assembled cited-factor set (gh#729, ADR-0026, R-4). A
/// suggestion cites a <b>set</b> of factors with exactly one <b>primary</b> (the headline) and zero-or-more
/// supporting; the primary is the single <b>smallest-timeframe</b> factor — gh#592's <c>min</c> rule, so a 5-minute
/// read is a scalp's headline and a 60-minute one a swing's. The N=1 "set of one" is the degenerate case every
/// suggestion is today: that one factor is its own primary.
/// </summary>
/// <remarks>
/// Pure and <b>entity-free</b> — it ranks factors from their timeframes alone (generic over the caller's payload),
/// so issuance derives <c>IsPrimary</c> through here rather than hand-setting it, and the rule is unit-testable off
/// the database. It does <b>not</b> assemble the set (that is gated on gh#595's output contract, ADR-0026) — it only
/// ranks one already assembled.
/// </remarks>
public static class CitedFactorSet
{
    /// <summary>
    /// Pairs each factor with whether it is the <b>primary</b> — the single smallest-timeframe one. Ties break to the
    /// <b>first</b> in sequence, so exactly one factor is ever primary (the invariant the DB's one-primary partial
    /// unique index backstops). Input order is preserved; an empty input yields an empty result.
    /// </summary>
    /// <typeparam name="T">The caller's factor payload — the rule reads only its timeframe.</typeparam>
    /// <param name="factors">The assembled factors, in any order.</param>
    /// <param name="timeframeMinutes">Reads a factor's timeframe in minutes; the smallest is the headline.</param>
    /// <returns>Each factor with its derived <c>IsPrimary</c> flag, in the input order.</returns>
    public static IReadOnlyList<CitedFactorPrimary<T>> DerivePrimary<T>(
        IEnumerable<T> factors, Func<T, int> timeframeMinutes)
    {
        ArgumentNullException.ThrowIfNull(factors);
        ArgumentNullException.ThrowIfNull(timeframeMinutes);

        IReadOnlyList<T> ordered = factors as IReadOnlyList<T> ?? [.. factors];
        if (ordered.Count == 0)
        {
            return [];
        }

        // The smallest timeframe is the headline (gh#592); the FIRST factor achieving it is the single primary, so a
        // tie is broken deterministically and the "exactly one primary" invariant holds.
        int smallestTimeframe = ordered.Min(timeframeMinutes);
        int primaryIndex = ordered
            .Select((factor, index) => (factor, index))
            .First(candidate => timeframeMinutes(candidate.factor) == smallestTimeframe)
            .index;

        return [.. ordered.Select((factor, index) => new CitedFactorPrimary<T>(factor, index == primaryIndex))];
    }
}

/// <summary>
/// A cited factor paired with whether the min-rule made it the suggestion's <b>primary</b> (gh#729). The caller maps
/// this back onto its own factor representation — the domain rule never touches the persisted entity.
/// </summary>
/// <typeparam name="T">The caller's factor payload.</typeparam>
/// <param name="Factor">The factor that was ranked.</param>
/// <param name="IsPrimary">Whether it is the single primary — the smallest-timeframe factor.</param>
public readonly record struct CitedFactorPrimary<T>(T Factor, bool IsPrimary);
