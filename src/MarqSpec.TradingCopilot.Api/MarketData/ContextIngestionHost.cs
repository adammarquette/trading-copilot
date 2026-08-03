using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The hosted service that drives <b>cross-asset context</b> ingestion (gh#496, of gh#411; R-1): on startup it
/// resolves each configured context symbol and runs a <see cref="ContextSubscriptionSupervisor"/> per contract,
/// feeding the backbone until the host stops. Opt-in — with no configured symbols it does nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate host from <see cref="MarketDataIngestionHost"/>, and that is the point.</b> gh#496's sharpest
/// acceptance criterion is that <i>a Finnhub outage must not disturb ProjectX futures ingestion</i> — cross-asset
/// context must never become a trading dependency. Running context on its own <see cref="BackgroundService"/>
/// makes that structural: the two loops share no task, no scope, and no venue client, so nothing that happens here
/// can reach the tradeable feed.
/// </para>
/// <para>
/// <b>Everything is caught, deliberately.</b> The default <c>BackgroundServiceExceptionBehavior</c> is
/// <c>StopHost</c> — an exception escaping <see cref="ExecuteAsync"/> takes the <i>whole application</i> down,
/// including the quote feed, the auto-flatten watchdog and the kill switch. A missing Finnhub token is enough to
/// throw while the source is being constructed, so an uncaught failure here would turn "context data is not
/// configured" into "the trading platform will not start". The catch-all below degrades to <b>no context data</b>
/// and says so, which is the only acceptable failure direction for an optional colour feed (engineering §9).
/// </para>
/// </remarks>
public sealed class ContextIngestionHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<ContextIngestionOptions> _options;
    private readonly ILogger<ContextIngestionHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened for the run.</param>
    /// <param name="options">The context-ingestion configuration.</param>
    /// <param name="logger">The logger.</param>
    public ContextIngestionHost(
        IServiceProvider services,
        IOptions<ContextIngestionOptions> options,
        ILogger<ContextIngestionHost> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A clean stop.
        }
        catch (Exception error)
        {
            // See the class remarks: letting this escape would stop the ENTIRE host, taking the tradeable quote
            // feed and the safety-critical services with it — because an optional colour feed failed.
            _logger.LogError(
                error,
                "Cross-asset context ingestion failed and is stopping; the platform continues WITHOUT context data. "
                + "Tradeable market data and execution are unaffected.");
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // Shared with the quote host: under compose an unset array element arrives as an empty string, so an
        // all-blank list must normalise to empty ("disabled"), and a symbol beyond index 0 must survive (gh#304).
        string[] symbols = MarketDataIngestionHost.NormalizeSymbols(_options.Value.Symbols);
        if (symbols.Length == 0)
        {
            _logger.LogInformation(
                "Cross-asset context ingestion is disabled — no symbols configured (ContextIngestion:Symbols).");
            return;
        }

        await using AsyncServiceScope scope = _services.CreateAsyncScope();

        IContextMarketDataSource? source = scope.ServiceProvider.GetService<IContextMarketDataSource>();
        if (source is null)
        {
            // Configured symbols but no source registered: say so rather than sit silent, since the operator asked
            // for context data and will otherwise wonder why none arrives.
            _logger.LogWarning(
                "Cross-asset context symbols are configured but no context source is registered; ingesting nothing.");
            return;
        }

        ContextTradeIngestionService ingestion = scope.ServiceProvider.GetRequiredService<ContextTradeIngestionService>();
        ILogger<ContextSubscriptionSupervisor> supervisorLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<ContextSubscriptionSupervisor>>();

        ContextSubscriptionSupervisor supervisor =
            new(ingestion, _options.Value.ReconnectDelay, supervisorLogger);

        List<Task> subscriptions = [];
        foreach (string symbol in symbols)
        {
            ResolvedContract contract;
            try
            {
                contract = await source.ResolveContractAsync(InstrumentId.Parse(symbol), stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // One unresolvable symbol must not sink the others -- the same guard the quote host holds.
                _logger.LogError(
                    error, "Could not resolve context symbol '{Symbol}'; skipping its subscription.", symbol);
                continue;
            }

            _logger.LogInformation(
                "Subscribing to context prints for {Symbol} ({Contract}).", symbol, contract.Contract);
            subscriptions.Add(supervisor.RunAsync(source, contract.Contract, stoppingToken));
        }

        await Task.WhenAll(subscriptions);
    }
}
