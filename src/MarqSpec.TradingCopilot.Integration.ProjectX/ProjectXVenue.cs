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
    /// <summary>
    /// How many quotes may queue for a slow consumer before the oldest are dropped. A quote is a snapshot of the
    /// current best bid/ask, so shedding stale ones beats growing without bound on a live tick stream.
    /// </summary>
    private const int QuoteBufferSize = 1_024;

    private readonly IProjectXApiClient _api;
    private readonly IProjectXWebSocketClient _webSocket;
    private readonly bool _live;

    /// <summary>Creates the adapter over a configured gateway client.</summary>
    /// <param name="api">The gateway's REST client.</param>
    /// <param name="webSocket">The gateway's realtime client.</param>
    /// <param name="dataTier">
    /// Which market-data universe these credentials are entitled to. Required rather than defaulted: the wrong
    /// tier returns an <b>empty</b> contract universe rather than an error, so a silent default would surface
    /// much later as an unresolvable instrument.
    /// </param>
    public ProjectXVenue(IProjectXApiClient api, IProjectXWebSocketClient webSocket, ProjectXDataTier dataTier)
    {
        _api = api;
        _webSocket = webSocket;
        _live = dataTier == ProjectXDataTier.Live;
    }

    /// <inheritdoc />
    public VenueId Id { get; } = VenueId.Parse("projectx");

    /// <summary>
    /// What this adapter can actually deliver <b>through <see cref="ITradingVenue"/></b>. The gateway itself does
    /// more — depth, account streaming, OCO brackets, order modify, trailing stops — but none of that is
    /// reachable through the venue-neutral contract yet, and advertising it would defeat the purpose of the
    /// capability model: a caller checking before it commits would pick a path that cannot work.
    /// </summary>
    public VenueCapabilities Capabilities { get; } = VenueCapabilities.Of(
        VenueCapability.HistoricalBars
        | VenueCapability.Quotes
        | VenueCapability.ClosePosition);

    /// <inheritdoc />
    public async Task<VenueContractId> ResolveContractAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken = default)
    {
        List<ClientModels.Contract> matches =
            [.. await _api.SearchContractsAsync(instrument.Symbol, _live, cancellationToken)];

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

        string contractKey = ProjectXMapping.ToContractKey(contract, Id);
        (ClientModels.AggregateBarUnit unit, int number) = ProjectXMapping.ToBarUnit(barSize);

        IEnumerable<ClientModels.AggregateBar> bars = await _api.GetHistoricalBarsAsync(
            contractKey,
            from.UtcDateTime,
            to.UtcDateTime,
            unit,
            number,
            limit: 1000,
            live: _live,
            includePartialBar: false,
            cancellationToken: cancellationToken);

        return [.. bars.Select(ProjectXMapping.ToBar)];
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Quote> StreamQuotesAsync(
        VenueContractId contract,
        CancellationToken cancellationToken = default)
    {
        // Validate eagerly, so a bad capability or a foreign contract fails at the call rather than on the first
        // read, long after the caller has moved on.
        Capabilities.Require(VenueCapability.Quotes);

        return ReadQuotesAsync(ProjectXMapping.ToContractKey(contract, Id), cancellationToken);
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
            await _api.GetOpenPositionsAsync(ProjectXMapping.ToAccountId(account, Id), cancellationToken);

        return [.. positions.Select(position => ProjectXMapping.ToPositionSnapshot(position, Id))];
    }

    /// <inheritdoc />
    public async Task<PlacedOrder> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Type == OrderType.TrailingStop)
        {
            // The neutral ticket carries no trail distance, so the gateway's trailPrice would go unset and the
            // order would arrive malformed. Refuse through the capability seam rather than send it.
            Capabilities.Require(VenueCapability.TrailingStops);
        }

        ClientModels.PlaceOrderRequest payload = new()
        {
            AccountId = ProjectXMapping.ToAccountId(request.Account, Id),
            ContractId = ProjectXMapping.ToContractKey(request.Contract, Id),
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
            ProjectXMapping.ToAccountId(account, Id),
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

        int accountId = ProjectXMapping.ToAccountId(account, Id);
        string contractKey = ProjectXMapping.ToContractKey(contract, Id);

        ClientModels.ClosePositionResponse response =
            await _api.ClosePositionAsync(accountId, contractKey, cancellationToken);

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
            position => string.Equals(position.ContractId, contractKey, StringComparison.Ordinal));

        return open is null
            ? new PositionSnapshot(account, contract, 0, new Price(0m))
            : ProjectXMapping.ToPositionSnapshot(open, Id);
    }

    private async IAsyncEnumerable<Quote> ReadQuotesAsync(
        string contractKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<Quote> quotes = Channel.CreateBounded<Quote>(new BoundedChannelOptions(QuoteBufferSize)
        {
            // A stale best bid/ask is worth less than the current one, so shed the oldest under back-pressure
            // rather than let a slow consumer grow the buffer without limit.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        void OnPriceUpdate(object? sender, ClientModels.PriceUpdate update)
        {
            // One hub carries every subscribed contract, so updates are filtered back down to this one.
            if (string.Equals(update.Symbol, contractKey, StringComparison.Ordinal))
            {
                quotes.Writer.TryWrite(ProjectXMapping.ToQuote(update));
            }
        }

        // Registration and cleanup share this iterator's lifecycle: attaching here means a sequence that is never
        // enumerated cannot leave a handler and a filling channel behind.
        _webSocket.PriceUpdateReceived += OnPriceUpdate;

        try
        {
            await _webSocket.ConnectMarketHubAsync(cancellationToken);
            await _webSocket.SubscribeToPriceUpdatesAsync(contractKey, cancellationToken);

            await foreach (Quote quote in quotes.Reader.ReadAllAsync(cancellationToken))
            {
                yield return quote;
            }
        }
        finally
        {
            // Runs on every exit, cancellation included, so a dropped subscription cannot leak a handler.
            _webSocket.PriceUpdateReceived -= OnPriceUpdate;
            await _webSocket.UnsubscribeFromPriceUpdatesAsync(contractKey, CancellationToken.None);
        }
    }
}
