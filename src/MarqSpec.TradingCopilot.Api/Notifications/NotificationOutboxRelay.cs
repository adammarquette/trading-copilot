using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Delivers what the outbox owes (gh#437 ⇒ gh#400): reads undelivered rows, hands each to the delivery chain,
/// and stamps it sent. Without this the rows <see cref="OutboxNotificationChannel"/> writes are never read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The guarantee is "no dropped page", not exactly-once.</b> Delivery is attempted first and the row stamped
/// afterwards, so a crash <i>between</i> the transport accepting and the stamp landing re-delivers on the next
/// pass rather than losing the page. Stamping first would close that duplicate window and open a worse one — a
/// page marked sent that never went. The chosen direction is deliberate: a repeated page is an annoyance, a
/// missed one is the failure R-13 exists to prevent.
/// </para>
/// <para>
/// Across passes it <i>is</i> idempotent, which is what the dedup key buys. The row is the ledger and its
/// <c>DedupKey</c> is the primary key, so one incident is one row, and a stamped row is never re-read.
/// </para>
/// <para>
/// <b>Every delivery is isolated.</b> One wedged incident must not strand the pages queued behind it — the
/// flatten path can raise several at once, and the second is not less urgent for the first being broken.
/// </para>
/// </remarks>
public sealed class NotificationOutboxRelay
{
    private readonly TradingCopilotDbContext _database;
    private readonly INotificationChannel _delivery;
    private readonly TimeProvider _clock;
    private readonly ILogger<NotificationOutboxRelay> _logger;

    /// <summary>Creates the relay.</summary>
    /// <param name="database">The scoped database holding the outbox.</param>
    /// <param name="delivery">The delivery chain — queue → dedup → transport, so this never awaits a network.</param>
    /// <param name="clock">The clock, injected so the delivered stamp is testable.</param>
    /// <param name="logger">The logger.</param>
    public NotificationOutboxRelay(
        TradingCopilotDbContext database,
        INotificationChannel delivery,
        TimeProvider clock,
        ILogger<NotificationOutboxRelay> logger)
    {
        _database = database;
        _delivery = delivery;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Delivers everything the outbox currently owes.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many rows were delivered and stamped on this pass.</returns>
    public async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        // Oldest first: an incident raised earlier is not overtaken by a later one. The (DeliveredAt, CreatedAt)
        // index serves this predicate and ordering directly.
        List<NotificationOutboxRecord> owed = await _database.NotificationOutbox
            .Where(row => row.DeliveredAt == null)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        if (owed.Count == 0)
        {
            return 0;
        }

        int delivered = 0;

        foreach (NotificationOutboxRecord row in owed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Counted before the attempt, so a row that wedges the transport every pass is DISCOVERABLE rather
            // than looking untouched. A page stuck at a rising attempt count is the signal an operator needs.
            row.Attempts++;

            bool accepted;
            try
            {
                accepted = await _delivery.SendAsync(
                    new Notification(row.Severity, row.Title, row.Body, row.DedupKey), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // a real shutdown stops the pass; the row stays owed and the next process takes it
            }
            catch (Exception error)
            {
                // Isolated per row -- see the class remarks. The row stays owed and is retried next pass.
                _logger.LogError(
                    error, "Delivering outbox notification {DedupKey} threw; it stays owed and will be retried.", row.DedupKey);
                continue;
            }

            if (!accepted)
            {
                // Fail-safe: an unaccepted page is still owed. Marking it sent would lose it silently, which is
                // the exact failure the outbox exists to close.
                _logger.LogWarning(
                    "The delivery chain did not accept outbox notification {DedupKey} (attempt {Attempts}); it stays owed.",
                    row.DedupKey, row.Attempts);
                continue;
            }

            row.DeliveredAt = _clock.GetUtcNow();
            delivered++;
        }

        // One commit for the pass: the attempt counts and the delivered stamps land together, so a crash here
        // re-delivers (safe) rather than stranding a half-updated ledger.
        await _database.SaveChangesAsync(cancellationToken);

        if (delivered > 0)
        {
            _logger.LogInformation("Relayed {Delivered} of {Owed} outbox notification(s).", delivered, owed.Count);
        }

        return delivered;
    }
}
