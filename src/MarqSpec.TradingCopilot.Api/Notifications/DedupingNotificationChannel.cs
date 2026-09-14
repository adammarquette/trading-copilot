using System.Collections.Concurrent;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Collapses a repeating condition into <b>one</b> notification per incident (gh#243, ADR-0019 §*The noise
/// budget*), delegating the actual delivery to the channel it wraps.
/// </summary>
/// <remarks>
/// <para>
/// The signals underneath repeat relentlessly: the auto-flatten scheduler re-emits its escalation roughly every
/// <b>15 s</b> while exposure persists and the watchdog every <b>20 s</b>, so a 30-minute outage produces about
/// 120 events. Forwarded naively that is 120 pushes — and a pager that cries 120 times gets muted, at which point
/// it is strictly worse than no pager, because it manufactures confidence. Deduplication here is a correctness
/// requirement, not a refinement.
/// </para>
/// <para>
/// <b>A decorator rather than logic inside the Pushover adapter.</b> ADR-0019 asked for dedup "in the adapter";
/// putting it one layer out means every future adapter — Discord (gh#100), web push (ADR-0010) — inherits it
/// instead of reimplementing it, and it can be unit-tested without a transport.
/// </para>
/// <para>
/// The nagging itself belongs to Pushover's Emergency retry, deliberately: ADR-0019 has Alertmanager send a P1
/// <i>once</i> and lets the channel repeat, because both repeating is double-nagging.
/// </para>
/// </remarks>
public sealed class DedupingNotificationChannel : INotificationChannel, IIncidentKeyRegistry
{
    private readonly INotificationChannel _inner;
    private readonly ILogger<DedupingNotificationChannel> _logger;

    // Incidents currently reported. Bounded by the number of things that can concurrently be wrong -- a handful
    // of account/instrument pairs -- and cleared by ResolveAsync, so no eviction policy is needed.
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    /// <summary>Creates the decorator.</summary>
    /// <param name="inner">The channel that actually delivers.</param>
    /// <param name="logger">The logger.</param>
    public DedupingNotificationChannel(INotificationChannel inner, ILogger<DedupingNotificationChannel> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_reported.ContainsKey(notification.DedupKey))
        {
            _logger.LogDebug("Suppressed a repeat of incident {Incident} — already reported.", notification.DedupKey);
            return false;
        }

        bool sent = await _inner.SendAsync(notification, cancellationToken);

        // Record ONLY on success. A failed push never reached the operator, so treating it as "already told them"
        // would let one transient outage silently cost the entire incident.
        if (sent)
        {
            _reported[notification.DedupKey] = 0;
        }

        return sent;
    }

    /// <inheritdoc />
    public async Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken)
    {
        // gh#300: the two halves of a resolve are decided separately, because when the cancel fails they pull in
        // opposite directions.
        //
        // RE-ARM unconditionally. This is local state and its failure mode is a duplicate page, which is the
        // safe direction; holding the key back because the transport could not confirm a cancel would suppress
        // the NEXT genuine incident as a stale duplicate -- silence, which is what this system exists to remove.
        if (ReleaseIncident(dedupKey))
        {
            _logger.LogInformation("Incident {Incident} resolved.", dedupKey);
        }

        // FORWARD unconditionally, even when this layer had nothing recorded. The pump retries a failed cancel
        // by calling this method again, and by then the re-arm above has already cleared the key -- an early
        // return would swallow every retry before it reached the transport. The transport no-ops cheaply when it
        // holds no receipt, so the redundant call costs nothing.
        return await _inner.ResolveAsync(dedupKey, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The re-arm above, exposed on its own (gh#1077). It is the half of a resolve that <b>cannot be recovered by
    /// anyone else</b>: the outbox has no row for a resolve and no producer reliably retries one, so a release
    /// that is lost is permanent silence on this key. <see cref="QueuedNotificationChannel"/> calls this — and
    /// only this, never the transport half — when a resolve could not be enqueued, so no outcome of that queue
    /// can leave a key held for the life of the process.
    /// </remarks>
    public bool ReleaseIncident(string dedupKey) => _reported.TryRemove(dedupKey, out _);
}
