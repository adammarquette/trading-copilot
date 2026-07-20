using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.Integration.ProjectX;

/// <summary>
/// The v1 venue adapter: ProjectX / TopstepX behind <see cref="ITradingVenue"/> (R-17). Every ProjectX-specific
/// detail stops here — the two SignalR hubs, the success-flag error convention, integer account ids, and how
/// practice-vs-live is expressed — so the core sees only the venue-neutral model.
/// </summary>
public sealed class ProjectXVenue : ITradingVenue
{
    private readonly IProjectXApiClient _api;
    private readonly IProjectXWebSocketClient _webSocket;

    /// <summary>Creates the adapter over a configured gateway client.</summary>
    /// <param name="api">The gateway's REST client.</param>
    /// <param name="webSocket">The gateway's realtime client.</param>
    public ProjectXVenue(IProjectXApiClient api, IProjectXWebSocketClient webSocket)
    {
        _api = api;
        _webSocket = webSocket;
    }

    /// <inheritdoc />
    public VenueId Id { get; } = VenueId.Parse("projectx");

    /// <inheritdoc />
    public VenueCapabilities Capabilities { get; } = VenueCapabilities.Of(
        VenueCapability.HistoricalBars
        | VenueCapability.Quotes
        | VenueCapability.MarketDepth
        | VenueCapability.AccountStreaming
        | VenueCapability.BracketOrders
        | VenueCapability.TrailingStops
        | VenueCapability.ModifyOrder
        | VenueCapability.ClosePosition);

    /// <inheritdoc />
    public async Task<VenueContractId> ResolveContractAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken = default)
    {
        List<ClientModels.Contract> matches =
            [.. await _api.SearchContractsAsync(instrument.Symbol, cancellationToken: cancellationToken)];

        // Prefer the front month the gateway marks active; a search can also return expired or back months.
        ClientModels.Contract? contract = matches.Find(c => c.ActiveContract) ?? matches.FirstOrDefault();

        return contract is null
            ? throw new ProjectXVenueException($"No ProjectX contract matches instrument '{instrument}'.")
            : VenueContractId.Create(Id, contract.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bar>> GetBarsAsync(
        VenueContractId contract,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan barSize,
        CancellationToken cancellationToken = default)
    {
        Capabilities.Require(VenueCapability.HistoricalBars);

        (ClientModels.AggregateBarUnit unit, int number) = ProjectXMapping.ToBarUnit(barSize);

        IEnumerable<ClientModels.AggregateBar> bars = await _api.GetHistoricalBarsAsync(
            contract.Key,
            from.UtcDateTime,
            to.UtcDateTime,
            unit,
            number,
            cancellationToken: cancellationToken);

        return [.. bars.Select(ProjectXMapping.ToBar)];
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Quote> StreamQuotesAsync(
        VenueContractId contract,
        CancellationToken cancellationToken = default)
    {
        Capabilities.Require(VenueCapability.Quotes);

        Channel<Quote> quotes = Channel.CreateUnbounded<Quote>(new UnboundedChannelOptions { SingleReader = true });

        void OnPriceUpdate(object? sender, ClientModels.PriceUpdate update)
        {
            // One hub carries every subscribed contract, so updates are filtered back down to this one.
            if (string.Equals(update.Symbol, contract.Key, StringComparison.Ordinal))
            {
                quotes.Writer.TryWrite(ProjectXMapping.ToQuote(update));
            }
        }

        // Attached before the caller begins enumerating, so ticks arriving in between are buffered, not dropped.
        _webSocket.PriceUpdateReceived += OnPriceUpdate;

        return ReadQuotesAsync(quotes, contract, OnPriceUpdate, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        // The full roster, not just the tradable ones -- the switcher filters, settings shows everything.
        IEnumerable<ClientModels.TradingAccount> accounts =
            await _api.GetAccountsAsync(onlyActiveAccounts: false, cancellationToken);

        return [.. accounts.Select(account => ProjectXMapping.ToVenueAccount(account, Id))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(
        VenueAccountId account,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ClientModels.Position> positions =
            await _api.GetOpenPositionsAsync(ProjectXMapping.ToAccountId(account), cancellationToken);

        return [.. positions.Select(position => ProjectXMapping.ToPositionSnapshot(position, Id))];
    }

    /// <inheritdoc />
    public async Task<PlacedOrder> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        ClientModels.PlaceOrderRequest payload = new()
        {
            AccountId = ProjectXMapping.ToAccountId(request.Account),
            ContractId = request.Contract.Key,
            Side = ProjectXMapping.ToClientSide(request.Side),
            Type = ProjectXMapping.ToClientType(request.Type),
            Size = request.Quantity,
            LimitPrice = request.LimitPrice?.Value,
            StopPrice = request.StopPrice?.Value,
        };

        ClientModels.PlaceOrderResponse response = await _api.PlaceOrderAsync(payload, cancellationToken);

        if (!response.Success)
        {
            throw new ProjectXVenueException(
                $"ProjectX rejected the order: {response.ErrorMessage ?? "no reason given"}.",
                response.ErrorCode);
        }

        if (response.OrderId is not { } orderId)
        {
            // Accepted but unidentified leaves nothing to cancel or flatten against -- treat it as a failure.
            throw new ProjectXVenueException("ProjectX accepted the order but returned no order id.");
        }

        return new PlacedOrder(
            request.Account,
            orderId.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task CancelOrderAsync(
        VenueAccountId account,
        string venueOrderId,
        CancellationToken cancellationToken = default)
    {
        ClientModels.CancelOrderResponse response = await _api.CancelOrderAsync(
            ProjectXMapping.ToAccountId(account),
            ProjectXMapping.ToOrderId(venueOrderId),
            cancellationToken);

        if (!response.Success)
        {
            throw new ProjectXVenueException(
                $"ProjectX refused to cancel order {venueOrderId}: {response.ErrorMessage ?? "no reason given"}.",
                response.ErrorCode);
        }
    }

    /// <inheritdoc />
    public async Task<PositionSnapshot> ClosePositionAsync(
        VenueAccountId account,
        VenueContractId contract,
        CancellationToken cancellationToken = default)
    {
        Capabilities.Require(VenueCapability.ClosePosition);

        int accountId = ProjectXMapping.ToAccountId(account);

        ClientModels.ClosePositionResponse response =
            await _api.ClosePositionAsync(accountId, contract.Key, cancellationToken);

        if (!response.Success)
        {
            throw new ProjectXVenueException(
                $"ProjectX refused to close {contract}: {response.ErrorMessage ?? "no reason given"}.",
                response.ErrorCode);
        }

        // Read the position back rather than assuming the close worked: the venue is the source of truth for
        // whether we are flat, and auto-flatten reconciles against exactly this (ADR-0013).
        IEnumerable<ClientModels.Position> remaining = await _api.GetOpenPositionsAsync(accountId, cancellationToken);
        ClientModels.Position? open = remaining.FirstOrDefault(
            position => string.Equals(position.ContractId, contract.Key, StringComparison.Ordinal));

        return open is null
            ? new PositionSnapshot(account, contract, 0, new Price(0m))
            : ProjectXMapping.ToPositionSnapshot(open, Id);
    }

    private async IAsyncEnumerable<Quote> ReadQuotesAsync(
        Channel<Quote> quotes,
        VenueContractId contract,
        EventHandler<ClientModels.PriceUpdate> handler,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await _webSocket.ConnectMarketHubAsync(cancellationToken);
            await _webSocket.SubscribeToPriceUpdatesAsync(contract.Key, cancellationToken);

            await foreach (Quote quote in quotes.Reader.ReadAllAsync(cancellationToken))
            {
                yield return quote;
            }
        }
        finally
        {
            // Detach on any exit -- cancellation included -- so a dropped subscription cannot leak a handler.
            _webSocket.PriceUpdateReceived -= handler;
            await _webSocket.UnsubscribeFromPriceUpdatesAsync(contract.Key, CancellationToken.None);
        }
    }
}
