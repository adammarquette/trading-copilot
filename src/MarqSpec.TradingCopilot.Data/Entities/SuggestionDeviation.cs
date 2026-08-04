namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// Which trade parameters the operator's <see cref="SuggestionDispositionKind.Modified"/> take differs from the
/// <see cref="Suggestion"/> on (gh#549, R-9) — the field-level shape of the taken-vs-suggested delta the R-9 learning
/// loop aggregates over. A multi-select, so it is a <c>[Flags]</c> value stored in one integer and sliced with a
/// bitwise test, mirroring <see cref="SuggestionPassReason"/>.
/// </summary>
/// <remarks>
/// <see cref="None"/> (zero) is the correct, valid value for a <see cref="SuggestionDispositionKind.Taken"/> take
/// (nothing deviated) and for a <see cref="SuggestionDispositionKind.Passed"/> disposition (never taken), so the
/// column carries <b>no</b> refusable-zero check. The set is computed once at take time by exact decimal comparison —
/// <see cref="SuggestionDisposition.ForTake"/> — so every consumer reads the same verdict rather than re-deriving it.
/// </remarks>
[Flags]
public enum SuggestionDeviation
{
    /// <summary>The take matched the suggestion on every compared field — an unmodified <see cref="SuggestionDispositionKind.Taken"/>.</summary>
    None = 0,

    /// <summary>The submitted entry price differs from the suggested one.</summary>
    Entry = 1 << 0,

    /// <summary>The submitted working (protective) stop differs from the suggested stop.</summary>
    Stop = 1 << 1,

    /// <summary>The submitted take-profit target differs from the suggested one (including removing it entirely).</summary>
    Target = 1 << 2,

    /// <summary>The submitted size differs from the suggested size.</summary>
    Size = 1 << 3,
}
