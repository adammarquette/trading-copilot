using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Delivers what the outbox owes (gh#400): reads undelivered rows, sends them, and stamps them delivered.
/// </summary>
/// <remarks>
/// <para>
/// <b>It delivers synchronously, and that is the point.</b> gh#289's queue returned "accepted" the instant a
/// notification was handed over — correct for protecting the R-13 hot path, and useless for a ledger, because a
/// row must not be marked delivered on the strength of "accepted". The relay is background work already, so it
/// can afford to wait for the real answer, and only a real success stamps
/// <see cref="NotificationOutboxRecord.DeliveredAt"/>.
/// </para>
/// <para>
/// <b>The hot path is still protected — better than before.</b> A producer now writes a local row (or, on the
/// <c>Enlist</c> path, adds it to a transaction it was already committing) and never waits on a network. Unlike
/// the in-memory queue this replaces, what it wrote survives a crash.
/// </para>
/// <para>
/// <b>A failed delivery leaves the row owed.</b> Attempts are counted and the row is retried next pass rather
/// than dropped, so a transport outage delays a page instead of losing it — which is the whole reason the outbox
/// exists.
/// </para>
/// </remarks>
public sealed class NotificationRelayHost : BackgroundService
{
    /// <summary>How often the outbox is drained. Fast enough that a page is not visibly delayed.</summary>
    private static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>How many owed notifications one pass attempts, so a backlog cannot monopolise a pass.</summary>
    private const int BatchSize = 20;

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<NotificationRelayHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a fresh scope per pass resolves the database from it.</param>
    /// <param name="clock">The clock, injected so the delivery stamp is testable.</param>
    /// <param name="logger">The logger.</param>
    public NotificationRelayHost(IServiceProvider services, TimeProvider clock, ILogger<NotificationRelayHost> logger)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Notification relay started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A FRESH scope per pass: the DbContext is scoped, so holding one for the host's lifetime would
                // leak tracked entities and keep polling disposed services on teardown (the gh#169 shape).
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    await DrainAsync(scope, stoppingToken);
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop
            }
            catch (ObjectDisposedException)
            {
                break; // the root provider is being torn down -- exit cleanly (gh#169)
            }
            catch (Exception error)
            {
                // Structural fault: log, back off, retry. Nothing was stamped, so nothing is lost.
                _logger.LogWarning(error, "Notification relay pass failed; retrying after {Delay}.", PollInterval);
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Delivers one batch of owed notifications. <b>Public so a deterministic test can drive exactly one pass</b>
    /// — the same affordance the queue's <c>DrainPendingAsync</c> provided, for the same reason: a suite that
    /// asserts on what was sent must be able to make sending happen, without racing a timer.
    /// </summary>
    /// <param name="scope">The scope to resolve the database and delivery chain from.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many rows were confirmed delivered.</returns>
    public async Task<int> DrainAsync(AsyncServiceScope scope, CancellationToken cancellationToken)
    {
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

        // The delivery chain BELOW the seam: dedup -> transport. Resolved as the concrete deduping channel rather
        // than INotificationChannel, because that seam now resolves to the OUTBOX -- asking for the interface here
        // would hand the relay back to the thing it is draining.
        DedupingNotificationChannel delivery = scope.ServiceProvider.GetRequiredService<DedupingNotificationChannel>();

        List<NotificationOutboxRecord> owed = await database.NotificationOutbox
            .Where(row => row.DeliveredAt == null)
            .OrderBy(row => row.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        int delivered = 0;
        foreach (NotificationOutboxRecord row in owed)
        {
            row.Attempts++;

            if (await delivery.SendAsync(
                new Notification(row.Severity, row.Title, row.Body, row.DedupKey), cancellationToken))
            {
                // Only a REAL success stamps it. "Accepted" is not "delivered" -- that distinction is the reason
                // this relay exists rather than the queue it replaces.
                row.DeliveredAt = _clock.GetUtcNow();
                delivered++;
            }
            else
            {
                _logger.LogWarning(
                    "Notification {DedupKey} was not delivered (attempt {Attempts}); it stays owed and retries.",
                    row.DedupKey, row.Attempts);
            }
        }

        if (owed.Count > 0)
        {
            // One save after the batch: attempt counts and delivery stamps land together, and a crash mid-batch
            // leaves the survivors owed -- the safe direction.
            await database.SaveChangesAsync(cancellationToken);
        }

        return delivered;
    }
}
