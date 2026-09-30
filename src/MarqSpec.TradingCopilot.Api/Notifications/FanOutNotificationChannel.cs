using MarqSpec.TradingCopilot.Domain.Notifications;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Delivers one notification through several transports at once, each behind its <b>own</b>
/// <see cref="DedupingNotificationChannel"/> (gh#1157, ADR-0019) — how Pushover and Discord share the single
/// transport slot of the outbox → queue → dedup → transport chain without becoming a parallel path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-lane dedup memory is the point.</b> One shared memory above the fan-out would record an incident as
/// "told" as soon as <i>any</i> transport accepted it, so a Discord success would silence a Pushover page that had
/// failed — the pager muted by the advisory copy. With a memory per lane, the auto-flatten's next re-emission
/// (~15 s) reaches exactly the lane that has not yet delivered, and does not re-post to the one that has.
/// </para>
/// <para>
/// It is both the channel and the <see cref="IIncidentKeyRegistry"/> the queue releases keys through (gh#1077), so
/// an out-of-band release reaches <b>every</b> lane's memory; a release that missed one lane would leave that
/// transport suppressing the operator's next outage.
/// </para>
/// <para>
/// Lanes run concurrently, so the Pushover send is never gated on Discord. This awaits every lane, though, so the
/// single-reader pump waits for the SLOWEST one: the Discord lane is wrapped in <see cref="BoundedNotificationChannel"/>
/// so that wait is capped. Each lane is failure-tolerant and never throws, apart from a genuine caller
/// cancellation, which propagates.
/// </para>
/// </remarks>
public sealed class FanOutNotificationChannel : INotificationChannel, IIncidentKeyRegistry
{
    private readonly DedupingNotificationChannel[] _lanes;

    /// <summary>Creates the fan-out.</summary>
    /// <param name="lanes">One dedup-wrapped transport per destination, in delivery order.</param>
    public FanOutNotificationChannel(IReadOnlyList<DedupingNotificationChannel> lanes)
    {
        ArgumentNullException.ThrowIfNull(lanes);

        _lanes = [.. lanes];
    }

    /// <inheritdoc />
    /// <returns><see langword="true"/> if at least one lane accepted it for delivery.</returns>
    public async Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        bool[] results = await Task.WhenAll(_lanes.Select(lane => lane.SendAsync(notification, cancellationToken)));
        return results.Any(accepted => accepted);
    }

    /// <inheritdoc />
    /// <returns>
    /// <see langword="true"/> only when <b>every</b> lane confirmed the incident closed; a lane that could not
    /// must not be masked by a sibling's success, or its page keeps nagging (gh#300).
    /// </returns>
    public async Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken)
    {
        bool[] results = await Task.WhenAll(_lanes.Select(lane => lane.ResolveAsync(dedupKey, cancellationToken)));
        return results.All(closed => closed);
    }

    /// <inheritdoc />
    public bool ReleaseIncident(string dedupKey)
    {
        // Every lane, not short-circuited: `Any` over a lazy select would stop at the first held key.
        bool[] released = _lanes.Select(lane => lane.ReleaseIncident(dedupKey)).ToArray();
        return released.Any(held => held);
    }
}
