namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// The trailing drawdown floor — the account-ending line, and the reason <b>buying power is not the risk budget</b>
/// (R-5). A $50K account with a $2,000 trailing max-loss fails below a ~$48,000 floor, and that floor ratchets up
/// behind the account's high-water mark but never comes back down.
/// </summary>
/// <remarks>
/// The load-bearing variable is <i>how</i> it trails, and it is a property of the <b>account</b>, not the firm:
/// Topstep is end-of-day only, while Apex sells both. See the wiki's prop-firm rules page.
/// </remarks>
public sealed record TrailingDrawdown
{
    private TrailingDrawdown(TrailingMode mode, decimal trailingAmount, decimal? locksAt, decimal floor)
    {
        Mode = mode;
        TrailingAmount = trailingAmount;
        LocksAt = locksAt;
        Floor = floor;
    }

    /// <summary>How the floor trails.</summary>
    public TrailingMode Mode { get; }

    /// <summary>How far behind the high-water mark the floor sits.</summary>
    public decimal TrailingAmount { get; }

    /// <summary>
    /// The ceiling the floor stops trailing at, if the account has one — Topstep locks at the starting balance,
    /// Apex intraday accounts at start + $100. <see langword="null"/> trails without limit.
    /// </summary>
    public decimal? LocksAt { get; }

    /// <summary>The current floor. Equity at or below this fails the account.</summary>
    public decimal Floor { get; }

    /// <summary>Opens a trailing drawdown at the account's starting balance.</summary>
    /// <param name="mode">End-of-day or intraday trailing.</param>
    /// <param name="trailingAmount">The trailing max-loss amount; must be positive.</param>
    /// <param name="startingBalance">The account's starting balance.</param>
    /// <param name="locksAt">The ceiling the floor stops trailing at, if any.</param>
    /// <returns>The opening drawdown state.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="trailingAmount"/> is not positive.</exception>
    public static TrailingDrawdown Start(
        TrailingMode mode,
        decimal trailingAmount,
        decimal startingBalance,
        decimal? locksAt = null)
    {
        if (trailingAmount <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trailingAmount), trailingAmount, "The trailing amount must be positive.");
        }

        return new TrailingDrawdown(mode, trailingAmount, locksAt, Cap(startingBalance - trailingAmount, locksAt));
    }

    /// <summary>
    /// Advances an <b>intraday</b>-trailing floor against live equity — unrealized profit included, which is what
    /// makes these accounts unforgiving: being up $1,000 on an open trade lifts the floor by $1,000, so giving it
    /// back puts you nearer the floor at an unchanged realized balance. A no-op in end-of-day mode.
    /// </summary>
    /// <param name="currentEquity">Current equity, including unrealized P&amp;L.</param>
    /// <returns>The advanced drawdown, or this one unchanged.</returns>
    public TrailingDrawdown AdvanceIntraday(decimal currentEquity)
    {
        return Mode == TrailingMode.Intraday ? RaiseTo(currentEquity - TrailingAmount) : this;
    }

    /// <summary>
    /// Advances an <b>end-of-day</b>-trailing floor on the closing realized balance. A no-op intraday — an EOD
    /// floor is known at the open and does not move during the session.
    /// </summary>
    /// <param name="closingBalance">The session's closing realized balance.</param>
    /// <returns>The advanced drawdown, or this one unchanged.</returns>
    public TrailingDrawdown AdvanceAtClose(decimal closingBalance)
    {
        return Mode == TrailingMode.EndOfDay ? RaiseTo(closingBalance - TrailingAmount) : this;
    }

    /// <summary>The distance from <paramref name="equity"/> down to the floor — the real risk budget.</summary>
    /// <param name="equity">Current equity, including unrealized P&amp;L.</param>
    /// <returns>The headroom, never negative.</returns>
    public decimal HeadroomFrom(decimal equity)
    {
        return Math.Max(0m, equity - Floor);
    }

    /// <summary>Whether <paramref name="equity"/> has hit the floor. Touching it is a breach, not just crossing.</summary>
    /// <param name="equity">Current equity, including unrealized P&amp;L.</param>
    /// <returns><see langword="true"/> if the account is at or below the floor.</returns>
    public bool IsBreachedBy(decimal equity)
    {
        return equity <= Floor;
    }

    private TrailingDrawdown RaiseTo(decimal candidateFloor)
    {
        // The floor ratchets: it rises to the capped candidate, and never falls back.
        decimal next = Math.Max(Floor, Cap(candidateFloor, LocksAt));

        return new TrailingDrawdown(Mode, TrailingAmount, LocksAt, next);
    }

    private static decimal Cap(decimal value, decimal? locksAt)
    {
        return locksAt is null ? value : Math.Min(value, locksAt.Value);
    }
}
