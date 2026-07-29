using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Records a notification <b>durably</b> instead of holding it in memory (gh#400): the outbox half of the
/// alerting path, drained by the relay host (still to land).
/// </summary>
/// <remarks>
/// <para>
/// gh#289 moved delivery off the R-13 hot path with an in-process queue — right for latency, and it left a
/// durability hole: a hard crash before the pump drains loses whatever it held. Writing a row closes that,
/// because nothing counts as sent until the relay stamps <c>DeliveredAt</c>.
/// </para>
/// <para>
/// <b>Two ways in, and the difference is the guarantee</b> (gh#400, decided 2026-07-28):
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="SendAsync"/> — the <c>INotificationChannel</c> seam. Writes and commits on its own, so producers
/// are unchanged. Survives a crash before delivery, which is the window measured in <i>seconds</i>. It cannot
/// close the gap between a producer's own commit and this write, because by the time the seam is called that
/// transaction has already closed.
/// </item>
/// <item>
/// <see cref="Enlist"/> — for producers that need the stronger guarantee. Adds the row to the caller's
/// <c>DbContext</c> <b>without saving</b>, so it commits atomically with their state change and the gap does not
/// exist. Used by the safety-critical producers, where a dropped page has real consequence.
/// </item>
/// </list>
/// <para>
/// <b>Scoped</b>, because it writes through the scoped <c>DbContext</c> — unlike the singleton dedup/transport
/// chain below the relay, which holds the open-incident set across passes.
/// </para>
/// </remarks>
public sealed class OutboxNotificationChannel : INotificationChannel
{
    private readonly TradingCopilotDbContext _database;
    private readonly TimeProvider _clock;
    private readonly DedupingNotificationChannel _delivery;
    private readonly ILogger<OutboxNotificationChannel> _logger;

    /// <summary>Creates the channel over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="clock">The clock, injected so the recorded time is testable.</param>
    /// <param name="delivery">
    /// The delivery chain (queue → dedup → transport). Used only by <see cref="ResolveAsync"/>, to cancel a page
    /// that has <b>already gone out</b> — a send is deferred to the relay, but a resolve cannot be, because an
    /// unacknowledged Emergency page keeps nagging until it is cancelled.
    /// </param>
    /// <param name="logger">The logger.</param>
    public OutboxNotificationChannel(
        TradingCopilotDbContext database,
        TimeProvider clock,
        DedupingNotificationChannel delivery,
        ILogger<OutboxNotificationChannel> logger)
    {
        _database = database;
        _clock = clock;
        _delivery = delivery;
        _logger = logger;
    }

    /// <summary>
    /// Records the notification and commits it immediately. Never throws — the channel contract is that a
    /// notification failure can never fail the action that raised it.
    /// </summary>
    /// <param name="notification">The notification to record.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> when the intent is durably recorded (not when it is delivered).</returns>
    public async Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        try
        {
            Enlist(notification);
            await _database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // A page that cannot even be RECORDED is the worst case here, so it is logged at error rather than
            // swallowed -- but still not thrown: gh#243's contract is that alerting never fails the safety
            // action that raised it.
            _logger.LogError(
                error, "Could not record notification {DedupKey} to the outbox; it will NOT be delivered.",
                notification.DedupKey);
            return false;
        }
    }

    /// <summary>
    /// Closes an incident: withdraws it from the outbox if it has not gone out yet, and cancels it at the
    /// transport if it has.
    /// </summary>
    /// <remarks>
    /// <b>Withdrawing beats delivering-then-cancelling.</b> A condition that cleared before its page was ever
    /// sent should produce no page at all — waking someone for something already fixed is exactly the fatigue
    /// ADR-0019's noise budget exists to prevent. The delivered case still has to reach the transport, because a
    /// Pushover Emergency nags until acknowledged.
    /// </remarks>
    /// <param name="dedupKey">The incident key that has cleared.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> when the incident is definitively closed.</returns>
    public async Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken)
    {
        try
        {
            NotificationOutboxRecord? undelivered = await _database.NotificationOutbox
                .FirstOrDefaultAsync(row => row.DedupKey == dedupKey && row.DeliveredAt == null, cancellationToken);

            if (undelivered is not null)
            {
                _database.NotificationOutbox.Remove(undelivered);
                await _database.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Incident {DedupKey} cleared before its page was sent; withdrawn from the outbox undelivered.",
                    dedupKey);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // Fall through to the transport regardless: failing to withdraw is recoverable (the relay may send a
            // page for a cleared incident), but failing to CANCEL an already-delivered one leaves a pager nagging.
            _logger.LogWarning(error, "Could not withdraw {DedupKey} from the outbox; cancelling at the transport.", dedupKey);
        }

        // Always ask the transport too -- the row may already have been delivered, and a resolve must reach it.
        return await _delivery.ResolveAsync(dedupKey, cancellationToken);
    }

    /// <summary>
    /// Adds the notification to the caller's <c>DbContext</c> <b>without saving</b>, so it commits in the
    /// caller's transaction (gh#400's option B).
    /// </summary>
    /// <remarks>
    /// The caller <b>must</b> call <c>SaveChangesAsync</c> afterwards — that is the point: intent and state
    /// commit together or neither does. A producer that enlists and then never saves has recorded nothing, which
    /// is why this is used deliberately by specific producers rather than being the default path.
    /// </remarks>
    /// <param name="notification">The notification to record.</param>
    public void Enlist(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // Attach-or-ignore on the dedup key: an incident already owed is not owed twice. The DB key enforces it
        // regardless, but catching it here avoids a pointless failed round trip on the common re-fire.
        if (_database.NotificationOutbox.Local.Any(row => row.DedupKey == notification.DedupKey))
        {
            return;
        }

        _database.NotificationOutbox.Add(new NotificationOutboxRecord
        {
            DedupKey = notification.DedupKey,
            Severity = notification.Severity,
            Title = notification.Title,
            Body = notification.Body,
            CreatedAt = _clock.GetUtcNow(),
            DeliveredAt = null,
            Attempts = 0,
        });
    }
}
