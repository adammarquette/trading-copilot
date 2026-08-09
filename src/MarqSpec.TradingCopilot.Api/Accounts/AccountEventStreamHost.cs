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
            if (accountEvent is PositionEvent { NetQuantity: 0 } exit)
            {
                // A position going flat retires whatever protection stood for it (OCO-cancel-on-exit, gh#183) --
                // a dangling safety stop is a live resting order with no position behind it.
                await scope.ServiceProvider
                    .GetRequiredService<OcoExitService>()
                    .ProcessExitAsync(exit, cancellationToken);

                // The same flat signal closes a round trip, so journal it (gh#731) -- this is what makes
                // DailyRealizedReader and the consistency window read real money instead of zero. Runs AFTER the
                // protection retire (the safety action leads) and never blocks it: a journal failure is logged and
                // swallowed, because a missing Trade row must not leave a dangling protective leg at the venue.
                await JournalRoundTripSafelyAsync(scope.ServiceProvider, exit, cancellationToken);
            }
            else
            {
                await scope.ServiceProvider
                    .GetRequiredService<AccountEventIngestionService>()
                    .ProcessAsync(accountEvent, cancellationToken);
            }
        }
    }

    // The journal is a SECONDARY write behind the safety action: a failure here loses a Trade row (the readers
    // then under-report the day, which the operator can see) but must never abort the stream or prevent the next
    // event's protection retire. Cancellation still propagates -- that is a clean shutdown, not a fault.
    private async Task JournalRoundTripSafelyAsync(
        IServiceProvider scope, PositionEvent exit, CancellationToken cancellationToken)
    {
        try
        {
            await scope.GetRequiredService<TradeJournalService>().ProcessFlatAsync(exit, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "Journalling the round trip that closed on {Contract} for account {Account} failed; the position's "
                + "protection was still retired. The day's realized P&L will under-report until this is corrected.",
                exit.Contract, exit.Account);
        }
    }
}
