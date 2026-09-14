using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>
/// Checks whether a flatten actually worked (R-13). Firing the close is not the same as being flat: the order
/// can be rejected, partially filled, or accepted against a position that has since changed.
/// </summary>
/// <remarks>
/// The input is what the <b>venue</b> reports after the close — never local state. The venue is the source of
/// truth for whether we are flat (ADR-0013), and this is the check that makes that concrete.
/// </remarks>
public static class FlattenVerification
{
    /// <summary>Decides whether the account is flat, should be retried, or must be escalated.</summary>
    /// <param name="remaining">Positions as the venue reports them after the close attempt.</param>
    /// <param name="attemptsMade">How many close attempts have been made, including the one just verified.</param>
    /// <param name="maxAttempts">How many attempts are permitted before escalating; must be positive.</param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxAttempts"/> is not positive.</exception>
    public static FlattenVerdict Verify(
        IReadOnlyList<PositionSnapshot> remaining,
        int attemptsMade,
        int maxAttempts)
    {
        if (maxAttempts <= 0)
        {
            // Zero attempts would mean never retrying and never escalating -- a value that silently switches the
            // safety net off.
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts), maxAttempts, "At least one flatten attempt must be permitted.");
        }

        // Any non-zero net quantity is exposure carried into the close, long or short alike. A venue may report
        // the contract with a zero quantity rather than dropping the row, so emptiness alone is not the test.
        bool stillExposed = remaining.Any(position => !position.IsFlat);

        if (!stillExposed)
        {
            return FlattenVerdict.Flat;
        }

        return attemptsMade < maxAttempts ? FlattenVerdict.Retry : FlattenVerdict.Escalate;
    }
}
