using MarqSpec.TradingCopilot.Domain.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Realtime;

/// <summary>
/// Tails the event log and pushes each presentation signal live to every connected client over
/// <see cref="RealtimeHub"/> (gh#645, R-10). A read-only consumer of the log — it never appends, never mutates
/// state, and never touches the broker; it only fans events out. It follows the same hosted-consumer discipline as
/// <c>StopPromotionHost</c> (gh#153): a fresh DI scope per pass, a cursor committed per batch, and a clean exit on
/// the stop token — the last of which prevents the parallel-integration-suite <see cref="ObjectDisposedException"/>
/// cascade when the host provider is torn down before the token fires.
/// </summary>
public sealed class RealtimeEventLogFanoutHost : BackgroundService
{
    /// <summary>This consumer's cursor group in the event log (ADR-0001). Independent of any client's resume.</summary>
    public const string ConsumerGroup = "realtime-hub";

    private const int BatchSize = 256;
    private static readonly TimeSpan _idlePoll = TimeSpan.FromSeconds(1);

    private readonly IServiceProvider _services;
    private readonly IHubContext<RealtimeHub> _hub;
    private readonly ILogger<RealtimeEventLogFanoutHost> _logger;

    /// <summary>Creates the fan-out over the root provider (for a scoped event-log read per pass) and the hub
    /// context it broadcasts through.</summary>
    public RealtimeEventLogFanoutHost(
        IServiceProvider services, IHubContext<RealtimeHub> hub, ILogger<RealtimeEventLogFanoutHost> logger)
    {
        _services = services;
        _hub = hub;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long? cursor = null;

        // Suppress broadcasting the pre-existing backlog on a first-ever start (no committed cursor): those events
        // are HISTORY, not live, and pushing a historical kill-switch / flatten as a live safety-strip banner would
        // be a lie. Catch up to head silently, then go live. A restart (committed cursor) resumes live at once — its
        // catch-up is tiny and clients dedupe by sequence.
        bool broadcasting = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool caughtUp;

                // A fresh scope per pass: IEventLog is scoped (it holds a scoped DbContext), so resolving it once
                // for the lifetime of the host would capture a disposed context. Same rule every hosted consumer follows.
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
                    if (cursor is null)
                    {
                        long? committed = await log.GetCursorAsync(ConsumerGroup, stoppingToken);
                        cursor = committed ?? 0;
                        broadcasting = committed is not null; // a committed cursor is a restart -> resume live at once
                    }

                    EventPage page = await log.ReadAfterAsync(cursor.Value, BatchSize, stoppingToken);

                    // The hub is presentation-only, so a retention gap is not recoverable state to rebuild — a
                    // connected client simply missed some ticks. Log it and carry on from what the log returned.
                    if (page.Gap is not null)
                    {
                        _logger.LogInformation(
                            "Realtime fan-out cursor {Cursor} fell behind the retention window (oldest {Oldest}); resuming from the returned tail.",
                            page.Gap.RequestedAfterSequence, page.Gap.OldestAvailableSequence);
                    }

                    caughtUp = page.Events.Count == 0;
                    if (!caughtUp)
                    {
                        foreach (EventEnvelope evt in page.Events)
                        {
                            if (broadcasting && RealtimeEventCatalog.IsBroadcast(evt.Type))
                            {
                                await _hub.Clients.All.SendAsync(
                                    RealtimeEvent.ClientMethod, RealtimeEvent.From(evt), stoppingToken);
                            }

                            cursor = evt.Sequence;
                        }

                        await log.CommitCursorAsync(ConsumerGroup, cursor.Value, stoppingToken);
                    }

                    // Reached head: everything from here on is genuinely live, so start broadcasting.
                    if (page.Events.Count < BatchSize)
                    {
                        broadcasting = true;
                    }
                }

                if (caughtUp)
                {
                    await Task.Delay(_idlePoll, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop, not a fault
            }
            catch (ObjectDisposedException)
            {
                // The root provider was torn down (host shutdown / a test factory disposing between classes) before
                // the stop token fired, so the scoped resolve threw. Exit cleanly rather than cascade the failure.
                break;
            }
            catch (Exception error)
            {
                // Transient (a dropped DB connection, a serialization hiccup): the cursor is uncommitted, so nothing
                // is skipped. Back off and retry.
                _logger.LogWarning(error, "Realtime fan-out pass failed; retrying after {Delay}.", _idlePoll);
                await Task.Delay(_idlePoll, stoppingToken);
            }
        }
    }
}
