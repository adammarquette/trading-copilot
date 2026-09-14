namespace MarqSpec.TradingCopilot.Domain.Signals;

/// <summary>
/// The independent axis a <see cref="SoftSignalKind"/> belongs to (gh#762, ADR-0014). <b>Importance</b> (magnitude →
/// salience) and <b>Direction</b> (sentiment → R-9 learning) are orthogonal: an operator may hold one of each on the
/// same item — an <i>important</i> <b>and</b> <i>bearish</i> story — so each axis is stored, replaced, and cleared
/// independently (two filtered unique indexes back this). A <b>refusable zero</b>: <see cref="Unknown"/> is never a
/// real axis.
/// </summary>
public enum SoftSignalAxis
{
    /// <summary>Unset — never a real axis (the refusable-zero pattern).</summary>
    Unknown = 0,

    /// <summary>
    /// Importance — <see cref="SoftSignalKind.Star"/> / <see cref="SoftSignalKind.Mute"/>. Reweights <b>salience</b>:
    /// what surfaces and how prominently (the only axis that does).
    /// </summary>
    Importance = 1,

    /// <summary>
    /// Direction — <see cref="SoftSignalKind.ThumbsUp"/> / <see cref="SoftSignalKind.ThumbsDown"/>. A sentiment fact
    /// for the R-9 learning loop and the read surface; <b>salience-inert</b>, so it never reweights what surfaces.
    /// </summary>
    Direction = 2,
}
