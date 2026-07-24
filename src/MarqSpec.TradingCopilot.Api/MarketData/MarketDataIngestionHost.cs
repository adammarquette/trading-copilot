using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The hosted service that <b>drives</b> quote ingestion (gh#13, R-1): on startup it resolves each configured
/// symbol to a venue contract and runs a <see cref="QuoteSubscriptionSupervisor"/> per contract, keeping the
/// backbone fed until the host stops. Opt-in — with no configured symbols it does nothing.
/// </summary>
/// <remarks>
/// A <see cref="BackgroundService"/> is a singleton while the venue factory, ingestion service, and event log
/// are scoped, so it opens <b>one long-lived scope</b> for the run. Market data is not operator-scoped, so the
/// venue is built with <see cref="FirmConventions.None"/> (ingestion never reads account mode).
/// </remarks>
public sealed class MarketDataIngestionHost : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<IngestionOptions> _options;
    private readonly ILogger<MarketDataIngestionHost> _logger;

    /// <summary>Creates the host.</summary>
    /// <param name="services">The root provider — a scope is opened per run.</param>
    /// <param name="options">The ingestion configuration.</param>
    /// <param name="logger">The logger.</param>
    public MarketDataIngestionHost(
        IServiceProvider services,
        IOptions<IngestionOptions> options,
        ILogger<MarketDataIngestionHost> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string[] symbols = NormalizeSymbols(_options.Value.Symbols);
        if (symbols.Length == 0)
        {
            _logger.LogInformation("Market-data ingestion is disabled — no symbols configured (Ingestion:Symbols).");
            return;
        }

        // One scope for the whole run: the venue's clients are long-lived subscriptions, not per-request.
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        IProjectXVenueFactory venueFactory = scope.ServiceProvider.GetRequiredService<IProjectXVenueFactory>();
        QuoteIngestionService ingestion = scope.ServiceProvider.GetRequiredService<QuoteIngestionService>();
        ILogger<QuoteSubscriptionSupervisor> supervisorLogger =
            scope.ServiceProvider.GetRequiredService<ILogger<QuoteSubscriptionSupervisor>>();

        ITradingVenue venue = venueFactory.Create(FirmConventions.None);
        QuoteSubscriptionSupervisor supervisor =
            new(ingestion, _options.Value.ReconnectDelay, supervisorLogger);

        List<Task> subscriptions = [];
        foreach (string symbol in symbols)
        {
            ResolvedContract contract;
            try
            {
                contract = await venue.ResolveContractAsync(InstrumentId.Parse(symbol), stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // One unresolvable symbol must not sink the others -- log it and keep the rest subscribed.
                _logger.LogError(error, "Could not resolve '{Symbol}' to a contract; skipping its subscription.", symbol);
                continue;
            }

            _logger.LogInformation("Subscribing to quotes for {Symbol} ({Contract}).", symbol, contract.Contract);
            subscriptions.Add(supervisor.RunAsync(venue, contract.Contract, stoppingToken));
        }

        await Task.WhenAll(subscriptions);
    }

    /// <summary>
    /// Trims and drops blank symbols. Under compose an unset array element arrives as an empty string, so an
    /// all-blank list must normalise to <b>empty</b> — read as "ingestion disabled", never a subscription to
    /// nothing.
    /// </summary>
    /// <param name="configured">The configured symbols.</param>
    /// <returns>The non-blank, trimmed symbols.</returns>
    internal static string[] NormalizeSymbols(string[] configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return [.. configured
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.Trim())];
    }
}
