namespace MarqSpec.TradingCopilot.Domain.Signals;

/// <summary>
/// The kind of per-user feedback on a soft-signal / news item (gh#27, gh#762, ADR-0014). A <b>refusable zero</b>:
/// <see cref="Unknown"/> is never a real kind — feedback must declare its intent — refused at construction and by a
/// DB check. One entity (<c>SoftSignalFeedback</c>) unifies all kinds across <b>two independent axes</b>
/// (<see cref="SoftSignalAxis"/>): <b>importance</b> (<see cref="Star"/> / <see cref="Mute"/> → salience) and
/// <b>direction</b> (👍/👎 <see cref="ThumbsUp"/> / <see cref="ThumbsDown"/> → R-9 learning). Map a kind to its axis
/// with <see cref="SoftSignalKindExtensions.Axis"/>; an operator may hold at most one of each axis per item.
/// </summary>
public enum SoftSignalKind
{
    /// <summary>Unset — never a real kind; refused at construction and by a DB check.</summary>
    Unknown = 0,

    /// <summary>The operator marked the item important — <b>raises</b> the salience of similar future items (importance axis).</summary>
    Star = 1,

    /// <summary>The star's inverse — the operator down-weighted the item, <b>lowering</b> the salience of similar future items (never hiding them; ADR-0014 salience floor). Importance axis.</summary>
    Mute = 2,

    /// <summary>
    /// The operator rated the item's direction 👍 (bullish / positive) — the <b>direction</b> axis (gh#762). A stored
    /// sentiment fact for the R-9 learning loop and the read surface; <b>salience-inert</b>, so it never reweights
    /// what surfaces (importance does that).
    /// </summary>
    ThumbsUp = 3,

    /// <summary>The 👎 (bearish / negative) direction inverse of <see cref="ThumbsUp"/> (gh#762) — likewise salience-inert.</summary>
    ThumbsDown = 4,
}
