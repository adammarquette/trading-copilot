namespace MarqSpec.TradingCopilot.Domain.Signals;

/// <summary>
/// Maps a <see cref="SoftSignalKind"/> to the independent <see cref="SoftSignalAxis"/> it belongs to (gh#762,
/// ADR-0014) — the single source of truth for which kinds share an axis, so the store's per-axis uniqueness, the
/// write path's replace-within-axis, and salience's importance-only accumulation all agree on the split.
/// </summary>
public static class SoftSignalKindExtensions
{
    /// <summary>The axis a kind belongs to.</summary>
    /// <param name="kind">The feedback kind.</param>
    /// <returns>
    /// <see cref="SoftSignalAxis.Importance"/> for <see cref="SoftSignalKind.Star"/> / <see cref="SoftSignalKind.Mute"/>,
    /// <see cref="SoftSignalAxis.Direction"/> for <see cref="SoftSignalKind.ThumbsUp"/> / <see cref="SoftSignalKind.ThumbsDown"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is <see cref="SoftSignalKind.Unknown"/> or an undefined value — a kind with no axis is
    /// refused, mirroring the store's refusable zero rather than inventing an axis.
    /// </exception>
    public static SoftSignalAxis Axis(this SoftSignalKind kind) =>
        kind.TryAxis(out SoftSignalAxis axis)
            ? axis
            : throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "SoftSignalKind has no axis — it must be Star, Mute, ThumbsUp, or ThumbsDown.");

    /// <summary>
    /// The axis a kind belongs to, <b>without throwing</b> — for READ paths over stored rows, where an unexpected kind
    /// (a corrupt or future row the <c>Kind &lt;&gt; 0</c> check does not forbid) must be <b>skipped</b>, not surface a
    /// 500. <see cref="Axis"/> stays throwing for the write path, where a non-axis kind is a genuine caller error.
    /// </summary>
    /// <param name="kind">The feedback kind.</param>
    /// <param name="axis">The mapped axis when this returns <see langword="true"/>; <see cref="SoftSignalAxis.Unknown"/> otherwise.</param>
    /// <returns><see langword="true"/> for a mappable kind (Star / Mute / ThumbsUp / ThumbsDown); <see langword="false"/> for Unknown or any undefined value.</returns>
    public static bool TryAxis(this SoftSignalKind kind, out SoftSignalAxis axis)
    {
        switch (kind)
        {
            case SoftSignalKind.Star or SoftSignalKind.Mute:
                axis = SoftSignalAxis.Importance;
                return true;
            case SoftSignalKind.ThumbsUp or SoftSignalKind.ThumbsDown:
                axis = SoftSignalAxis.Direction;
                return true;
            default:
                axis = SoftSignalAxis.Unknown;
                return false;
        }
    }
}
