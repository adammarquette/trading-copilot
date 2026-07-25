namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The <b>execution</b> slice of the venue abstraction (R-17) — the only place an order reaches a broker. A
/// data-only provider does not implement it.
/// </summary>
/// <remarks>
/// This interface transmits; it does not decide. The risk model, execution-time re-validation, and sanity caps
/// (R-5 / R-12 / R-16) run before anything here is called, and enforcement never lives below this line.
/// </remarks>
public interface IOrderExecutor : IVenue
{
    /// <summary>Transmits an order to the venue.</summary>
    /// <param name="request">The ticket to send — already gated by the risk and sanity layers.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The venue's acknowledgement.</returns>
    Task<PlacedOrder> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancels a working order.</summary>
    /// <param name="account">The account the order sits on.</param>
    /// <param name="venueOrderId">The venue's order handle from <see cref="PlacedOrder.VenueOrderId"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the venue has accepted the cancellation.</returns>
    Task CancelOrderAsync(
        VenueAccountId account,
        string venueOrderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a position outright — the primitive auto-flatten is built on (R-13). Returns the venue's own
    /// post-close view so the caller reconciles against the venue rather than trusting a local belief (ADR-0013).
    /// </summary>
    /// <param name="account">The account holding the position.</param>
    /// <param name="contract">The contract to flatten.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The venue's position snapshot after the close.</returns>
    /// <exception cref="VenueCapabilityNotSupportedException">
    /// The venue does not grant <see cref="VenueCapability.ClosePosition"/>.
    /// </exception>
    Task<PositionSnapshot> ClosePositionAsync(
        VenueAccountId account,
        VenueContractId contract,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the orders <b>resting live at the venue</b> for an account (gh#183) — the venue's own truth, used by
    /// OCO-cancel-on-exit to find the protective legs still standing on a contract that has gone flat.
    /// </summary>
    /// <remarks>
    /// A <b>fail-closed default</b>: a venue that cannot list its working orders throws rather than silently
    /// reporting none, so a caller degrades <b>loudly</b> (R-17) instead of leaving a dangling leg it never saw.
    /// An adapter that can list overrides this.
    /// </remarks>
    /// <param name="account">The account whose working orders to list.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The orders resting at the venue for the account.</returns>
    /// <exception cref="NotSupportedException">This venue cannot list working orders.</exception>
    Task<IReadOnlyList<WorkingOrder>> GetWorkingOrdersAsync(
        VenueAccountId account,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This venue does not support listing working orders.");
}
