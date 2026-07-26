using System.Threading.Channels;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Accepts notifications without blocking the caller and delivers them on a background pump (gh#289, fixing
/// gh#243) — the decorator that keeps the send <b>off the flatten hot path</b>.
/// </summary>
/// <remarks>
/// <para>
/// gh#243 asserted that sending was off the hot path and it was not: the flatten awaited the send inline, so a
/// slow channel added its full latency to a pass on the <b>R-13 safety path</b> — and it did so precisely when a
/// position was already failing to close. gh#246 measured a 5-second channel turning a pass into 5.15 seconds.
/// </para>
/// <para>
/// Queueing rather than a bare fire-and-forget, for three reasons the issue called out:
/// </para>
/// <list type="bullet">
/// <item><b>Dedup stops racing itself.</b> The pump is single-threaded, so the "have I already reported this
/// incident?" check and the record of having reported it can no longer interleave — two escalations arriving
/// together used to be able to both pass the check and double-page.</item>
/// <item><b>A failed delivery is still a failed delivery.</b> The pump sees the real result, so
/// <see cref="DedupingNotificationChannel"/> below it still refuses to record an incident it did not manage to
/// report. A detached task whose result nobody read would have lost that.</item>
/// <item><b>Shutdown drains instead of dropping.</b> A page queued as the host stops is the one most worth
/// delivering — see <see cref="DrainPendingAsync"/>.</item>
/// </list>
/// <para>
/// <b>The contract shifts subtly and deliberately:</b> <see cref="SendAsync"/> returning <see langword="true"/>
/// now means <i>accepted for delivery</i>, not <i>delivered</i>. Callers on the safety path cannot wait for
/// delivery without reintroducing the defect, so the honest thing is to say so rather than to imply a guarantee
/// the design gives up on purpose.
/// </para>
/// </remarks>
public sealed class QueuedNotificationChannel : INotificationChannel
{
    private readonly INotificationChannel _inner;
    private readonly ILogger<QueuedNotificationChannel> _logger;
    private readonly Channel<Delivery> _queue;

    /// <summary>Creates the queueing decorator.</summary>
    /// <param name="inner">The channel that actually delivers — deduping, then the transport.</param>
    /// <param name="logger">The logger.</param>
    public QueuedNotificationChannel(INotificationChannel inner, ILogger<QueuedNotificationChannel> logger)
    {
        _inner = inner;
        _logger = logger;

        // Bounded, so a wedged transport cannot grow the queue without limit beside a trading process. DropWrite
        // keeps the OLDEST: during an incident the first page is the one that matters, and the rest are repeats
        // of it that dedup would collapse anyway. A drop is logged -- silence is what this whole area is about.
        _queue = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
    }

    /// <inheritdoc />
    /// <remarks>Enqueues and returns; it never waits for the transport. See the type remarks.</remarks>
    public Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!_queue.Writer.TryWrite(Delivery.Send(notification)))
        {
            _logger.LogError(
                "Notification queue is full — dropped a {Severity} for {Incident}. The transport is not draining.",
                notification.Severity, notification.DedupKey);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    /// <remarks>Enqueued like a send, so ordering with the send it clears is preserved.</remarks>
    public Task ResolveAsync(string dedupKey, CancellationToken cancellationToken)
    {
        if (!_queue.Writer.TryWrite(Delivery.Resolve(dedupKey)))
        {
            _logger.LogWarning("Notification queue is full — dropped the resolve for {Incident}.", dedupKey);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Delivers queued work until the queue is empty, and returns how many items went out.
    /// </summary>
    /// <remarks>
    /// Public because "flush what is pending, now" is a real operation, not a test hook: the pump uses it to
    /// drain on shutdown, and a harness that wants deterministic assertions uses it instead of racing a timer.
    /// It does <b>not</b> wait for anything not yet enqueued.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the drain itself, not an individual delivery.</param>
    /// <returns>How many items were delivered.</returns>
    public async Task<int> DrainPendingAsync(CancellationToken cancellationToken)
    {
        int delivered = 0;
        while (!cancellationToken.IsCancellationRequested && _queue.Reader.TryRead(out Delivery? item))
        {
            await DeliverAsync(item);
            delivered++;
        }

        return delivered;
    }

    /// <summary>Runs the pump until <paramref name="stoppingToken"/> is cancelled, then drains what is left.</summary>
    /// <param name="stoppingToken">The host's stopping token.</param>
    internal async Task PumpAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (Delivery item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await DeliverAsync(item);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Falling through to the drain below is the point: a page queued as the host stops is raised
            // BECAUSE something went wrong, so dropping it loses exactly the alert worth keeping.
        }

        int flushed = await DrainPendingAsync(CancellationToken.None);
        if (flushed > 0)
        {
            _logger.LogInformation("Flushed {Count} pending notification(s) on shutdown.", flushed);
        }
    }

    /// <summary>
    /// Delivers one item, absorbing any fault. A delivery that throws must never take the pump down — the first
    /// failure would otherwise silence every notification after it.
    /// </summary>
    private async Task DeliverAsync(Delivery item)
    {
        try
        {
            // CancellationToken.None deliberately: the caller's token belongs to the FLATTEN. Inheriting it would
            // let a finished pass -- or a stopping host -- cancel the very page explaining why it failed.
            if (item.Notification is not null)
            {
                await _inner.SendAsync(item.Notification, CancellationToken.None);
            }
            else
            {
                await _inner.ResolveAsync(item.DedupKey, CancellationToken.None);
            }
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Delivering a notification for {Incident} failed.", item.DedupKey);
        }
    }

    /// <summary>One queued unit of work: a notification to send, or an incident to close.</summary>
    private sealed record Delivery(Notification? Notification, string DedupKey)
    {
        public static Delivery Send(Notification notification) => new(notification, notification.DedupKey);

        public static Delivery Resolve(string dedupKey) => new(null, dedupKey);
    }
}
