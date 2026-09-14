using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// The pure decision at the heart of OCO-cancel-on-exit (R-11, gh#183, ADR-0007): given the orders resting at the
/// venue and the operator's <b>journaled resting entries</b>, which legs on a now-flat contract are <b>dangling
/// protection</b> to cancel.
/// </summary>
/// <remarks>
/// The distinction is by construction, not a price/type heuristic: every protective leg the venue spawns — the
/// native safety-stop bracket attached on fill, the promoted actual stop — is <b>never journaled as an
/// <c>Order</c> row</b>, while every entry the operator places always is. So on a flat contract, a venue-resting
/// order whose handle is <b>not</b> one of the journaled resting entries is a leg the just-closed position left
/// behind; a journaled entry is the operator's own pending order and is left standing. The caller only reaches
/// here once the contract is flat (<c>NetQuantity == 0</c>), so cancelling never touches a live position (R-11).
/// </remarks>
public static class OcoExitSelection
{
    /// <summary>
    /// The venue-resting legs on <paramref name="contract"/> to cancel — those not backed by a journaled entry.
    /// </summary>
    /// <param name="workingOrders">The orders resting at the venue for the account.</param>
    /// <param name="contract">The contract that has gone flat.</param>
    /// <param name="journaledEntryKeys">The venue handles of the operator's journaled resting entries — preserved.</param>
    /// <returns>The dangling protective legs to cancel.</returns>
    public static IReadOnlyList<WorkingOrder> LegsToCancel(
        IReadOnlyList<WorkingOrder> workingOrders,
        VenueContractId contract,
        IReadOnlySet<string> journaledEntryKeys)
    {
        ArgumentNullException.ThrowIfNull(workingOrders);
        ArgumentNullException.ThrowIfNull(journaledEntryKeys);

        return
        [
            .. workingOrders.Where(order =>
                order.Contract == contract && !journaledEntryKeys.Contains(order.VenueOrderKey)),
        ];
    }
}
