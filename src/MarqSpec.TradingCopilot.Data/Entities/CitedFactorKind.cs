namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// What a <see cref="CitedFactor"/> cites (data dictionary §6, R-4, gh#729): the two evidence shapes confluence is
/// assembled from (ADR-0026) — a repeated <see cref="Indicator"/> read across timeframes, or an entry near a ranked
/// price <see cref="Level"/>.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (the fail-closed-zero pattern, gh#60) — a DB check rejects it, so a
/// factor is never persisted without a kind. The two arms carry different columns, and a DB pairing check ties the
/// kind to the arm it fills (indicator ⇒ indicator columns; level ⇒ the level snapshot).
/// </remarks>
public enum CitedFactorKind
{
    /// <summary>Not a kind — the refusable zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>An indicator read that fired — the <c>Indicator</c> / <c>Period</c> arm.</summary>
    Indicator = 1,

    /// <summary>A price level the entry sat near — the immutable level-snapshot arm.</summary>
    Level = 2,
}
