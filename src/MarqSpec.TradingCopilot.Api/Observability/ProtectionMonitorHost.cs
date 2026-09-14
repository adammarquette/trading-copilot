using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Observability;

/// <summary>
/// Runs the <see cref="ProtectionMonitorService"/> on a timer (gh#370, ADR-0019): the periodic check that no live
/// position is standing with nothing at the exchange behind it.
/// </summary>
/// <remarks>
/// <para>
/// <b>30 seconds.</b> Fast enough that an unprotected position is caught within an ADR-0019 <c>for: 2m</c> window
/// with passes to spare, slow enough to be two venue calls per account per half-minute. This is a
/// <i>measurement</i> loop, not a protection mechanism — the native safety stop and the promotion watcher do the
/// protecting, and this reports on whether they actually did.
/// </para>
/// <para>
/// A fresh scope per pass, and a clean exit on shutdown — the gh#169 shape, for the same reason: a scoped
/// DbContext held for the host's lifetime leaks tracked entities and keeps polling disposed services when a test
/// host is torn down.
/// </para>
/// </remarks>
public sealed class ProtectionMonitorHost : BackgroundService
{
    /// <summary>How often the census is taken.</summary>
    private static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<ProtectionMonitorHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a per-pass scope resolves from it.</param>
    /// <param name="logger">The logger.</param>
    public ProtectionMonitorHost(IServiceProvider services, ILogger<ProtectionMonitorHost> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    ProtectionMonitorService monitor =
                        scope.ServiceProvider.GetRequiredService<ProtectionMonitorService>();
                    IProjectXVenueFactory venueFactory =
                        scope.ServiceProvider.GetRequiredService<IProjectXVenueFactory>();

                    ITradingVenue venue = venueFactory.Create(FirmConventions.None);
                    await monitor.ObserveAsync(venue, stoppingToken);
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop -- the host is shutting down
            }
            catch (ObjectDisposedException)
            {
                break; // the root provider is being torn down (host shutdown) -- exit cleanly (gh#169)
            }
            catch (Exception error)
            {
                // A measurement loop must never take the process with it: log, back off, retry. The census is
                // simply not published this pass, which is the same "unknown, not zero" stance the service takes.
                _logger.LogWarning(error, "Protection census pass failed; retrying after {Delay}.", PollInterval);
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }
}
