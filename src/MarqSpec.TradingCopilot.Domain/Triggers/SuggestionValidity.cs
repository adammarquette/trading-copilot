using MarqSpec.TradingCopilot.Domain.Flatten;

namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// When a suggestion stops being actionable (gh#544, R-4) — a <b>pure</b> policy: the configured validity window,
/// clamped so a suggestion can never outlive the session's pre-close auto-flatten deadline (R-13).
/// </summary>
/// <remarks>
/// <para>
/// The clamp is the point. A suggestion that stays live past the flatten deadline invites the operator to take a
/// position the auto-flatten is about to close — so the expiry is the earlier of *"the operator's window"* and
/// *"the moment this market gets flattened anyway"*. Like size and mode, this value is the <b>system's, never the
/// model's</b>.
/// </para>
/// <para>
/// The arithmetic is deliberately done in <b>market wall-clock spans</b>, mirroring <see cref="FlattenSchedule.Decide"/>:
/// convert the issue instant to Central, measure the distance to today's deadline there, and add that span back to the
/// original instant. Converting a deadline <i>back</i> to UTC is where daylight-saving bugs live, and this never does it.
/// </para>
/// </remarks>
public static class SuggestionValidity
{
    /// <summary>Computes when a suggestion issued at <paramref name="issuedAt"/> expires.</summary>
    /// <param name="issuedAt">When the suggestion was issued.</param>
    /// <param name="configuredValidity">The operator's configured window. Must be positive.</param>
    /// <param name="marketDeadline">
    /// The market's session deadline in <b>Central wall-clock</b>, or <see langword="null"/> when there is none to
    /// respect — an unknown market, or one whose auto-flatten the operator deliberately disabled (R-13).
    /// </param>
    /// <returns>The expiry instant — always strictly after <paramref name="issuedAt"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="configuredValidity"/> is not positive.</exception>
    public static DateTimeOffset ExpiresAt(
        DateTimeOffset issuedAt,
        TimeSpan configuredValidity,
        TimeOnly? marketDeadline)
    {
        if (configuredValidity <= TimeSpan.Zero)
        {
            // Unreachable in production -- SuggestionOptions validates on start -- but the DB CHECK
            // (ExpiresAt > CreatedAt) depends on this being positive, so state the precondition rather than
            // silently emit a row the database will refuse.
            throw new ArgumentOutOfRangeException(
                nameof(configuredValidity), configuredValidity, "The configured validity window must be positive.");
        }

        TimeSpan window = configuredValidity;

        // A disabled market has no deadline to beat (R-13 makes disabling deliberate and warned), and an unknown one
        // has no schedule at all -- in both cases the operator's window stands unclamped.
        if (marketDeadline is { } deadline)
        {
            DateTime marketNow = MarketClock.ToMarketTime(issuedAt);
            TimeSpan untilDeadline = (marketNow.Date + deadline.ToTimeSpan()) - marketNow;

            // Only clamp toward a deadline still AHEAD of us. Past it, today's flatten has already run and the
            // binding constraint is the next session's deadline -- which needs a trading calendar this domain does
            // not have. The configured window is minutes, so it cannot reach tomorrow's deadline anyway; and expiry
            // is not execution, so a late suggestion is still gated by R-12 at take-time.
            if (untilDeadline > TimeSpan.Zero && untilDeadline < window)
            {
                window = untilDeadline;
            }
        }

        return issuedAt + window;
    }
}
