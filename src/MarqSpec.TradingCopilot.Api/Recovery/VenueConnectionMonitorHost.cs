using MarqSpec.TradingCopilot.Domain.Observability;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// Watches the venue connection (ADR-0013, gh#209) and drives the <see cref="OrphanGuardService"/>: on a
/// <b>drop</b> it orphans the hidden synthetic stops, on a <b>reconnect</b> it re-arms them. It reconciles on the
/// first pass too, so a restart while orphaned stops linger (connection back) re-arms them, and a start while
/// down orphans the hidden ones.
/// </summary>
/// <remarks>
/// Polls rather than subscribes: the poll is simple, races nothing across the connection client's own threads,
/// and a brief drop that self-heals within a poll needs no action (the native safety stop covered it anyway). A
/// fresh scope is opened only on a transition — the guard's DbContext is scoped — and the loop exits cleanly on
/// shutdown (the gh#169 shape).
/// </remarks>
public sealed class VenueConnectionMonitorHost : BackgroundService
{
    /// <summary>How often to sample the venue connection state.</summary>
    private static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _services;
    private readonly ILogger<VenueConnectionMonitorHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — the connection seam and a per-transition scope resolve from it.</param>
    /// <param name="logger">The logger.</param>
    public VenueConnectionMonitorHost(IServiceProvider services, ILogger<VenueConnectionMonitorHost> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool? wasConnected = null; // reconcile on the first pass (null differs from either state)
        bool pendingRearm = false; // a re-arm left stops unverifiable (venue unreachable) -- retry while connected (gh#191)

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Resolve the connection seam LAZILY, not in the constructor: building it constructs the venue
                // client, which needs credentials the process may not have at startup (and a test host never
                // does). The other watchers defer their venue resolution the same way (gh#169).
                IVenueConnection connection = _services.GetRequiredService<IVenueConnection>();
                bool isConnected = connection.IsConnected;

                // Published EVERY pass, not only on a change (gh#370): a gauge that stops being written looks
                // identical to one whose value never changed, and this one is read by an alert whose whole job is
                // "how long has it been down". Re-stating it each pass also keeps the series alive so `for: 2m`
                // measures wall-clock rather than time-since-last-transition.
                _services.GetRequiredService<IExecutionMetrics>().SetVenueConnected(isConnected);

                // A change (or the first observation) reconciles the synthetic stops to the current liveness. While
                // connected, a re-arm that left stops unverifiable (venue unreachable) is retried each pass until it
                // resolves -- a since-closed position's orphaned stop must not linger orphaned forever (gh#191).
                if (wasConnected != isConnected || (isConnected && pendingRearm))
                {
                    await using AsyncServiceScope scope = _services.CreateAsyncScope();
                    OrphanGuardService guard = scope.ServiceProvider.GetRequiredService<OrphanGuardService>();

                    if (isConnected)
                    {
                        RearmOutcome outcome = await guard.RearmAsync(stoppingToken);
                        pendingRearm = outcome.StillOrphaned > 0;
                    }
                    else
                    {
                        await guard.OrphanAsync(stoppingToken);
                        pendingRearm = false;
                    }

                    wasConnected = isConnected;
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop
            }
            catch (ObjectDisposedException)
            {
                break; // the root provider is being torn down (host shutdown) -- exit cleanly (gh#169)
            }
            catch (Exception error)
            {
                // A transient fault must not kill the guard: log, back off, retry. wasConnected is unchanged, so
                // the next successful pass still reconciles.
                _logger.LogWarning(error, "Venue connection monitor pass failed; retrying after {Delay}.", PollInterval);
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }
}
