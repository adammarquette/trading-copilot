namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The venue-neutral account-event seam (R-17, gh#219): a realtime stream of order, position, and fill events for
/// the accounts the process's one credential set can reach. Like <see cref="IVenueConnection"/>, the underlying
/// user hub is <b>process-wide</b> (one credential set per process, ADR-0015), so this is a <b>singleton</b> seam
/// kept off the scoped <see cref="ITradingVenue"/>.
/// </summary>
/// <remarks>
/// Whether a venue can stream account events at all is the <see cref="VenueCapability.AccountStreaming"/>
/// capability on <see cref="ITradingVenue"/> — callers <c>Require</c> it at the call and degrade gracefully rather
/// than discover the gap mid-stream (R-17). This seam is the transport; the adapter translates the venue's own
/// payloads onto <see cref="AccountEvent"/> at its boundary.
/// </remarks>
public interface IAccountEventStream
{
    /// <summary>The venue this stream belongs to.</summary>
    VenueId Venue { get; }

    /// <summary>
    /// Connects the account stream, subscribes each account, and yields every event until cancelled. A stream that
    /// ends or throws is a drop the caller re-subscribes; cancellation is a clean stop.
    /// </summary>
    /// <param name="accounts">The accounts to subscribe — all under the process's one credential set.</param>
    /// <param name="cancellationToken">Cancels the subscription (a clean stop).</param>
    /// <returns>The neutral account events as they arrive.</returns>
    IAsyncEnumerable<AccountEvent> StreamAsync(
        IReadOnlyCollection<VenueAccountId> accounts, CancellationToken cancellationToken);
}
