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
    public static SoftSignalAxis Axis(this SoftSignalKind kind) => kind switch
    {
        SoftSignalKind.Star or SoftSignalKind.Mute => SoftSignalAxis.Importance,
        SoftSignalKind.ThumbsUp or SoftSignalKind.ThumbsDown => SoftSignalAxis.Direction,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "SoftSignalKind has no axis — it must be Star, Mute, ThumbsUp, or ThumbsDown."),
    };
}
