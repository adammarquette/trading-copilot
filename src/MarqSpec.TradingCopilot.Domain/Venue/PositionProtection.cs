namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// How many positions are open, and how many of those have <b>no protective stop resting at the venue</b>
/// (gh#370, ADR-0019).
/// </summary>
/// <param name="Open">Positions the venue reports with non-zero net exposure.</param>
/// <param name="Unprotected">
/// Of those, how many have no stop order resting on the same contract. <b>ADR-0019 makes this a P1</b>: a live
/// position with no exchange-held stop is the state the entire staged-stop model exists to prevent.
/// </param>
public readonly record struct ProtectionCensus(int Open, int Unprotected);

/// <summary>
/// Reconciles venue positions against venue working orders to find exposure that nothing is protecting
/// (gh#370).
/// </summary>
/// <remarks>
/// <para>
/// Pure and venue-truth-only: it compares what the venue says is <i>open</i> against what the venue says is
/// <i>resting</i>, and consults no local state. That is deliberate — every local record of protection
/// (<c>StopPlan</c> staging, an order row) is a belief about the venue, and this measurement exists precisely to
/// catch the case where that belief is wrong.
/// </para>
/// <para>
/// <b>Distinct from <c>trading.stops.orphaned</c></b>, which counts stops that <i>were</i> venue-held and were
/// moved to <c>Orphaned</c> on a connection drop. That is a known, handled transition. This counts exposure with
/// nothing standing behind it at all — including cases nothing ever recorded.
/// </para>
/// </remarks>
public static class PositionProtection
{
    /// <summary>Counts open and unprotected positions for one account.</summary>
    /// <param name="positions">What the venue reports open.</param>
    /// <param name="workingOrders">What the venue reports resting.</param>
    /// <returns>The census.</returns>
    public static ProtectionCensus Census(
        IReadOnlyList<PositionSnapshot> positions,
        IReadOnlyList<WorkingOrder> workingOrders)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(workingOrders);

        // A stop leg is one carrying a STOP price. A take-profit rests on the winning side and caps the gain --
        // it does nothing about the loss, so a position holding only one of those is exactly as unprotected as
        // one holding nothing.
        HashSet<string> protectedContracts =
        [
            .. workingOrders
                .Where(order => order.StopPrice is not null)
                .Select(order => order.Contract.Key),
        ];

        // Flat rows are reported by the venue and are not exposure. Counting them would make an idle account look
        // open, and an alert that fires on a flat book is one nobody keeps listening to.
        List<PositionSnapshot> open = [.. positions.Where(position => position.NetQuantity != 0)];

        // Matched per CONTRACT, never per account: a stop resting on NQ protects nothing about an MES position,
        // and counting stops against positions in aggregate would report a protected book while one instrument
        // sat naked.
        int unprotected = open.Count(position => !protectedContracts.Contains(position.Contract.Key));

        return new ProtectionCensus(open.Count, unprotected);
    }
}
