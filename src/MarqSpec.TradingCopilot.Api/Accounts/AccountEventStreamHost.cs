using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Accounts;

/// <summary>
/// Drives the account-event stream (R-17, gh#219): connects the venue's user hub for this process's accounts and
/// applies each order / fill event to the journal, so an order's terminal status and its fills stop being
/// invisible. Harmless with no accounts / no venue events, so it always runs.
/// </summary>
/// <remarks>
/// Resolves the streaming seam and the venue <b>lazily inside</b> the run, never in the constructor: building them
/// touches the venue client, which needs credentials the process (and every test host) may lack at startup — the
/// eager-injection crash the gh#212 work recorded. The capability is <c>Require</c>d through the seam at the call
/// (R-17): a venue that cannot stream is refused here, not discovered mid-stream. A fresh DI scope is opened
/// <b>per event</b> (no scoped dependency is held across the stream), and the loop exits cleanly on shutdown (the
/// gh#169 teardown shape): the <see cref="AccountEventSubscriptionSupervisor"/> handles the drop-vs-stop reconnect.
/// </remarks>
public sealed class AccountEventStreamHost : BackgroundService
{
    /// <summary>How long to wait before re-subscribing after a dropped stream.</summary>
    private static TimeSpan ReconnectDelay { get; } = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _services;
    private readonly ILogger<AccountEventStreamHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — the seam, the venue, and a per-event scope resolve from it.</param>
    /// <param name="logger">The logger.</param>
    public AccountEventStreamHost(IServiceProvider services, ILogger<AccountEventStreamHost> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AccountEventSubscriptionSupervisor supervisor = new(
            ReconnectDelay, _services.GetRequiredService<ILogger<AccountEventSubscriptionSupervisor>>());

        try
        {
            await supervisor.RunAsync(RunSessionAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // a clean stop
        }
        catch (ObjectDisposedException)
        {
            // the root provider is being torn down (host shutdown / WebApplicationFactory between tests) -- exit clean
        }
        catch (VenueCapabilityNotSupportedException)
        {
            // the configured venue cannot stream account events -- refused through the capability seam (R-17); the
            // host simply does nothing rather than retrying a gap that will never close.
            _logger.LogInformation("The venue does not support account streaming; the account-event host is idle.");
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        // Resolve the seam LAZILY -- building it touches the venue client (credentials), which a test host lacks.
        IAccountEventStream stream = _services.GetRequiredService<IAccountEventStream>();

        IReadOnlyList<VenueAccountId> accounts;
        await using (AsyncServiceScope discovery = _services.CreateAsyncScope())
        {
            // Refuse through the capability seam at the call (R-17): a venue that cannot stream throws here.
            ITradingVenue venue = discovery.ServiceProvider
                .GetRequiredService<IProjectXVenueFactory>()
                .Create(FirmConventions.None);
            venue.Capabilities.Require(VenueCapability.AccountStreaming);

            accounts = await discovery.ServiceProvider
                .GetRequiredService<AccountEventIngestionService>()
                .DiscoverAccountsAsync(cancellationToken);
        }

        if (accounts.Count == 0)
        {
            // No accounts under this process's credential set -- nothing to stream. Returning is a clean end; the
            // supervisor re-checks after the reconnect delay (a new account would be picked up then).
            return;
        }

        await foreach (AccountEvent accountEvent in stream.StreamAsync(accounts, cancellationToken))
        {
            // Fresh scope per event: no scoped dependency held across the stream, and a failed write is isolated.
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            await scope.ServiceProvider
                .GetRequiredService<AccountEventIngestionService>()
                .ProcessAsync(accountEvent, cancellationToken);
        }
    }
}
