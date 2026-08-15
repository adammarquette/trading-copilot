namespace MarqSpec.TradingCopilot.Domain.Journal;

/// <summary>The terminal fact an <c>Outcome</c> resolves over (gh#832, R-9).</summary>
public enum OutcomeBasis
{
    /// <summary>The unset zero — not a real basis; <c>TryResolve</c> refuses it (fail closed).</summary>
    Unknown = 0,

    /// <summary>A round trip closed; the signed realized P&amp;L carries the result.</summary>
    ClosedTrade = 1,

    /// <summary>The suggestion's validity window passed with no fill.</summary>
    ExpiredUnfilled = 2,

    /// <summary>The suggestion was passed / cancelled with no fill, before it could expire.</summary>
    Scratched = 3,
}

/// <summary>
/// Derives the <see cref="OutcomeResolution"/> from what the journal already holds (gh#832, R-9) — a pure function
/// over primitives, testable without a database, as the FIFO pairing policy (gh#759) is.
/// </summary>
/// <remarks>
/// The only real classification is a closed trade's <b>sign</b>: positive is a <see cref="OutcomeResolution.Win"/>,
/// negative a <see cref="OutcomeResolution.Loss"/>, and <b>exactly flat</b> a
/// <see cref="OutcomeResolution.NoFillScratch"/> — a round trip that netted zero is a scratch, no net result. The
/// unfilled dispositions map straight through. <b>Refuse-don't-guess</b> survives for the unresolvable: a closed
/// trade that carries no realized result, and a basis that is not a defined enum value, both return
/// <see langword="false"/> rather than inventing a resolution whose sign a report would trust.
/// </remarks>
public static class OutcomeResolutionPolicy
{
    /// <summary>Resolves an outcome from its terminal basis.</summary>
    /// <param name="basis">The terminal fact to resolve over.</param>
    /// <param name="realizedPnL">
    /// The closed trade's signed realized P&amp;L; ignored for the unfilled dispositions, required for
    /// <see cref="OutcomeBasis.ClosedTrade"/>.
    /// </param>
    /// <param name="resolution">The resolved value, or <see cref="OutcomeResolution.Unknown"/> when unresolvable.</param>
    /// <returns><see langword="true"/> when a real resolution was derived.</returns>
    public static bool TryResolve(OutcomeBasis basis, decimal? realizedPnL, out OutcomeResolution resolution)
    {
        resolution = basis switch
        {
            OutcomeBasis.ClosedTrade => ResolveClosedTrade(realizedPnL),
            OutcomeBasis.ExpiredUnfilled => OutcomeResolution.Expired,
            OutcomeBasis.Scratched => OutcomeResolution.NoFillScratch,
            _ => OutcomeResolution.Unknown,
        };

        return resolution != OutcomeResolution.Unknown;
    }

    private static OutcomeResolution ResolveClosedTrade(decimal? realizedPnL) =>
        realizedPnL switch
        {
            null => OutcomeResolution.Unknown, // refuse: a closed trade must carry its signed result
            > 0m => OutcomeResolution.Win,
            < 0m => OutcomeResolution.Loss,
            _ => OutcomeResolution.NoFillScratch, // exactly flat: a break-even round trip is a scratch
        };
}
