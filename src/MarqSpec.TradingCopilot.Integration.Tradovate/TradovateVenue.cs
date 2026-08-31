using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MarqSpec.Client.Tradovate;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.Extensions.Logging;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.Integration.Tradovate;

/// <summary>
/// The Tradovate venue adapter behind <see cref="ITradingVenue"/> (R-17, gh#41 / gh#977). It serves contract
/// resolution, account / position reads, <b>historical bars</b>, and <b>live quotes</b>; execution is ungranted
/// and refuses loudly through the capability seam (or as <see cref="NotSupportedException"/>) until its slice lands
/// (gh#977). Every Tradovate-specific detail — integer ids, an already-signed net position, demo-vs-live as the mode
/// source (a brokerage, gh#780), bars and quotes over the market-data socket — stops here so the core sees only the neutral model.
/// </summary>
public sealed class TradovateVenue : ITradingVenue
{
    /// <summary>How many quotes may queue for a slow consumer before the oldest are dropped — a stale best bid/ask is worth less than the current one.</summary>
    private const int QuoteBufferSize = 1_024;

    private readonly ITradovateApiClient _api;
    private readonly ITradovateWebSocketClient _webSocket;
    private readonly TradovateQuoteSubscriptions _subscriptions;
    private readonly ILogger<TradovateVenue> _logger;
    private readonly FirmConventions _conventions;

    /// <summary>Creates the adapter over a configured Tradovate client and a firm's conventions.</summary>
    /// <param name="api">The Tradovate REST client (credentials and host from configuration).</param>
    /// <param name="webSocket">
    /// The Tradovate dual-socket WebSocket client — bars (and, later, quotes) ride the market-data socket. A
    /// process-wide singleton (one credential set per process, ADR-0015).
    /// </param>
    /// <param name="subscriptions">
    /// The process-wide register of live quote subscriptions, shared with the connection host. Required, not
    /// optional: a quote stream that does not register itself survives a host-driven reconnect as an open sequence
    /// that never ticks again, and nothing raises.
    /// </param>
    /// <param name="conventions">
    /// What the firm behind this login has declared (R-14, gh#60). Tradovate is a brokerage, so these are
    /// <see cref="FirmConventions.ForBrokerage"/> and mode follows the venue's own host.
    /// <see cref="FirmConventions.None"/> resolves every account to <see cref="TradingMode.Undeclared"/> — the
    /// intended failure direction (tradeable nowhere), not an accident.
    /// </param>
    /// <param name="logger">The logger. A dropped socket makes a failed wire unsubscribe the ordinary teardown
    /// case rather than an error to raise, and a swallow nobody can see is how that stops being observable.</param>
    public TradovateVenue(
        ITradovateApiClient api,
        ITradovateWebSocketClient webSocket,
        TradovateQuoteSubscriptions subscriptions,
        ILogger<TradovateVenue> logger,
        FirmConventions conventions)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(webSocket);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(conventions);

        _api = api;
        _webSocket = webSocket;
        _subscriptions = subscriptions;
        _logger = logger;
        _conventions = conventions;
    }

    /// <inheritdoc />
    public VenueId Id { get; } = VenueId.Parse("tradovate");

    /// <summary>
    /// What this adapter delivers through <see cref="ITradingVenue"/>: <see cref="VenueCapability.HistoricalBars"/>
    /// and <see cref="VenueCapability.Quotes"/>. Contract resolution and account / position reads are not
    /// capability-gated and work regardless; execution and position-close are ungranted, so those paths refuse at the
    /// seam rather than doing something partial (their slice lands in gh#977).
    /// </summary>
    public VenueCapabilities Capabilities { get; } = VenueCapabilities.Of(VenueCapability.HistoricalBars | VenueCapability.Quotes);

    /// <summary>
    /// The Tradovate derivation-logic version (ADR-0009). <b>1</b> — the initial read slice: mode from the configured
    /// host through the firm's brokerage conventions (gh#780), a signed net position, and integer ids. Bump on any
    /// change to how raw Tradovate facts become derived values (mode, stage).
    /// </summary>
    public int AdapterLogicVersion => 1;

    /// <inheritdoc />
    public async Task<ResolvedContract> ResolveContractAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken = default)
    {
        // Exact-name resolution for now: FindContract matches a full contract name (e.g. ESM24). Resolving a bare
        // product root (ES) to its front month belongs to the market-data slice (gh#977), where bars and quotes need
        // the same contract handling — so this maps what the client returns and fails loudly on no match.
        ClientModels.Contract? contract = await _api.FindContractAsync(instrument.Symbol, cancellationToken);

        return contract is null
            ? throw new TradovateVenueException($"No Tradovate contract matches instrument '{instrument}'.")
            : TradovateMapping.ToResolvedContract(contract, instrument, Id);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The returned series is capped (<see cref="TradovateMapping"/> pins the chart request's element count, as the
    /// ProjectX adapter does); a range wanting more bars than the cap is truncated at its far edge.
    /// </remarks>
    public async Task<IReadOnlyList<Bar>> GetBarsAsync(
        VenueContractId contract,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan barSize,
        CancellationToken cancellationToken = default)
    {
        Capabilities.Require(VenueCapability.HistoricalBars);

        // Map (and validate) before touching the socket: a foreign-venue contract or a bad range throws here, never
        // opening a connection for a request that was invalid.
        ClientModels.ChartRequest request = TradovateMapping.ToChartRequest(contract, from, to, barSize, Id);

        // The market-data socket is a process-wide singleton owned by the connection host (gh#977's connection slice),
        // NOT by this per-call read. Connecting here would be unsafe: ConnectMarketDataAsync is not idempotent — it
        // tears the transport down and reconnects WITHOUT replaying subscriptions — so a bars read arriving mid-stream
        // would silently destroy another consumer's live quotes. Fail loudly if the socket is down; the host connects it.
        if (_webSocket.MarketDataState != ClientModels.ConnectionState.Connected)
        {
            throw new TradovateVenueException(
                "The Tradovate market-data socket is not connected; historical bars require the connection host to have "
                + "connected it first (gh#977). A bars read never manages the shared socket's lifecycle.");
        }

        IReadOnlyList<ClientModels.ChartBar> bars = await _webSocket.GetHistoricalBarsAsync(request, cancellationToken);

        // IMarketDataSource.GetBarsAsync promises ascending time order (it is the R-1 journaling / replay series), but
        // the socket returns bars in wire order — and md/getChart's multi-chart branch concatenates across chart
        // objects — so sort rather than trust the arrival order.
        return [.. bars.Select(TradovateMapping.ToBar).OrderBy(bar => bar.OpenTime)];
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Quote> StreamQuotesAsync(
        VenueContractId contract,
        CancellationToken cancellationToken = default)
    {
        // Eager (like the ProjectX adapter): a missing capability, or a foreign / non-numeric contract, fails at the
        // call, not on the first read. Everything after runs inside the iterator, so an unconsumed stream leaves no
        // handler or subscription behind.
        Capabilities.Require(VenueCapability.Quotes);
        long contractId = TradovateMapping.ToContractId(contract, Id);

        return ReadQuotesAsync(contractId, cancellationToken);
    }

    private async IAsyncEnumerable<Quote> ReadQuotesAsync(
        long contractId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The stream does NOT connect the shared, process-wide market-data socket: ConnectMarketDataAsync is not
        // idempotent (it reconnects without replaying subscriptions), so connecting here would silently drop other
        // consumers' subscriptions. The connection host owns the socket (gh#977); fail loud if it is down.
        if (_webSocket.MarketDataState != ClientModels.ConnectionState.Connected)
        {
            throw new TradovateVenueException(
                "The Tradovate market-data socket is not connected; a quote stream requires the connection host to have "
                + "connected it first (gh#977).");
        }

        Channel<Quote> quotes = Channel.CreateBounded<Quote>(new BoundedChannelOptions(QuoteBufferSize)
        {
            // Shed the oldest under back-pressure rather than let a slow consumer grow the buffer without bound.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        // Tradovate's quote events are INCREMENTAL — a single event may carry only the bid, only the ask, or a last
        // trade, and every field is nullable — but the neutral Quote is a complete bid+ask snapshot. Hold the
        // last-known sides for this contract and emit a complete snapshot once both are known. A lock serializes the
        // update and the emit together, so quotes stay ordered even if the socket dispatches events concurrently.
        object gate = new();
        decimal? bid = null;
        decimal? ask = null;
        long? bidSize = null;
        long? askSize = null;
        DateTimeOffset? at = null;

        void OnQuote(object? sender, ClientModels.Quote quote)
        {
            // One socket carries every subscribed contract, so filter down to this one by the id we subscribed with.
            if (quote.ContractId != contractId)
            {
                return;
            }

            // Only a bid or ask entry moves top-of-book. Tradovate also sends trade / volume / high / settlement
            // frames that carry a fresh timestamp but no bid or ask; re-emitting an unchanged book under an advancing
            // timestamp would mislead a consumer reading Quote.Timestamp as "when the book last changed", so skip them.
            if (quote.BidPrice is null && quote.AskPrice is null)
            {
                return;
            }

            // Emitting under the lock is safe and cannot stall the socket's read loop: a bounded DropOldest channel
            // sheds the oldest and returns rather than waiting, and it does not run the reader's continuation inline
            // (AllowSynchronousContinuations stays false), so TryWrite neither blocks nor re-enters this handler.
            lock (gate)
            {
                if (quote.BidPrice is { } bidPrice)
                {
                    bid = bidPrice;
                    bidSize = quote.BidSize;
                }

                if (quote.AskPrice is { } askPrice)
                {
                    ask = askPrice;
                    askSize = quote.AskSize;
                }

                if (quote.Timestamp is { } timestamp)
                {
                    at = timestamp;
                }

                if (bid is { } currentBid && ask is { } currentAsk && at is { } lastChange)
                {
                    quotes.Writer.TryWrite(TradovateMapping.ToQuote(lastChange, currentBid, currentAsk, bidSize, askSize));
                }
            }
        }

        // Attach the handler BEFORE subscribing, so a tick published between the subscribe and the first read is not
        // lost. Registration and cleanup share this iterator's lifecycle: detach + unsubscribe on every exit
        // (cancellation included), so an abandoned stream cannot leave a handler and a filling channel behind.
        // The register -- not this method -- owns the wire subscribe and unsubscribe, because a claim and the wire
        // call it implies have to be one indivisible step: two consumers of the same contract otherwise race, and
        // the loser's late unsubscribe silently kills the winner's feed with nothing left to notice it (the client
        // forgets the key before sending, and the socket stays Connected, so neither replay path covers it).
        string subscribeKey = contractId.ToString(CultureInfo.InvariantCulture);
        bool holding = false;

        _webSocket.QuoteReceived += OnQuote;
        try
        {
            // Acquiring registers the claim BEFORE the wire subscribe, so a reconnect the connection host drives in
            // between still replays this contract; the manual connect path replays what is here, not what the client
            // remembers. A subscribe that throws rolls the claim back, so `holding` stays false and the teardown
            // below cannot decrement another consumer's count.
            await _subscriptions.AcquireAsync(
                subscribeKey, token => _webSocket.SubscribeQuoteAsync(subscribeKey, token), cancellationToken);
            holding = true;

            await foreach (Quote quote in quotes.Reader.ReadAllAsync(cancellationToken))
            {
                yield return quote;
            }
        }
        finally
        {
            _webSocket.QuoteReceived -= OnQuote;

            if (holding)
            {
                await ReleaseSafelyAsync(subscribeKey);
            }
        }
    }

    // Teardown very often happens BECAUSE the socket dropped, and the client refuses to send on a dead transport --
    // so a failed unsubscribe here is the ordinary case, not an error to surface: a consumer disposing its stream
    // must not be handed the venue's transport fault. It is not silent, though. The client removes its own record
    // of the key before it sends, so a failure leaves the contract subscribed at the venue with nothing consuming
    // it and nothing that will ever take it down -- wasted bandwidth rather than a safety problem, but the only
    // trace it leaves is this log line.
    private async Task ReleaseSafelyAsync(string subscribeKey)
    {
        try
        {
            await _subscriptions.ReleaseAsync(
                subscribeKey, () => _webSocket.UnsubscribeQuoteAsync(subscribeKey, CancellationToken.None));
        }
        catch (Exception error)
        {
            _logger.LogWarning(
                error,
                "Unsubscribing the Tradovate quote feed for contract {Contract} failed while a stream was torn "
                + "down; the contract may stay subscribed at the venue with nothing consuming it.",
                subscribeKey);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        // Tradovate is a brokerage (gh#780): mode follows the venue's own host, and there is one host per process, so
        // every discovered account takes the same demo/live flag. Resolve it once and REFUSE the whole discovery on an
        // unrecognised host — we cannot know whether such a host is practice or live, and mapping an account off an
        // unknown flag would let it persist as live-tradeable downstream (the fail-open the read-time Undeclared alone
        // does not prevent, because the raw flag is recomputed at write points).
        if (TradovateMapping.IsSimulatedHost(_api.ConfiguredHost) is not { } venueReportsSimulated)
        {
            throw new TradovateVenueException(
                "The configured Tradovate host is neither the demo nor the live host, so an account's practice-vs-live "
                + "mode cannot be classified. Configure a recognised Tradovate host.");
        }

        IReadOnlyList<ClientModels.Account> accounts = await _api.GetAccountsAsync(cancellationToken);
        IReadOnlyList<ClientModels.CashBalance> balances = await _api.GetCashBalancesAsync(cancellationToken);

        // Balance is a separate cash-balance read, not on the account, so join by account id. A single (settlement)
        // cash-balance row per account is assumed — the futures case; when several exist (multi-currency) the lowest
        // currency id is taken deterministically, pending fuller multi-currency support (gh#977). An account with none
        // reads a zero balance rather than failing the whole discovery.
        Dictionary<long, decimal> balanceByAccount = balances
            .GroupBy(balance => balance.AccountId)
            .ToDictionary(group => group.Key, group => group.OrderBy(balance => balance.CurrencyId).First().Amount);

        return
        [
            .. accounts.Select(account => TradovateMapping.ToVenueAccount(
                account,
                account.Id is { } id && balanceByAccount.TryGetValue(id, out decimal amount) ? amount : 0m,
                Id,
                _conventions,
                venueReportsSimulated)),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(
        VenueAccountId account,
        CancellationToken cancellationToken = default)
    {
        long accountId = TradovateMapping.ToAccountId(account, Id);

        // The client lists positions for the whole login, so filter to this account. A flat position (netPos 0) is
        // not an open position — skip it, matching the "one snapshot per contract with an open position" contract.
        IReadOnlyList<ClientModels.Position> positions = await _api.GetPositionsAsync(cancellationToken);

        return
        [
            .. positions
                .Where(position => position.AccountId == accountId && position.NetPos != 0)
                .Select(position => TradovateMapping.ToPositionSnapshot(position, Id)),
        ];
    }

    /// <inheritdoc />
    public Task<PlacedOrder> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The Tradovate venue is read-only in this increment; execution lands with gh#977 (slice 3).");

    /// <inheritdoc />
    public Task CancelOrderAsync(
        VenueAccountId account,
        string venueOrderId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The Tradovate venue is read-only in this increment; execution lands with gh#977 (slice 3).");

    /// <inheritdoc />
    public Task<PositionSnapshot> ClosePositionAsync(
        VenueAccountId account,
        VenueContractId contract,
        CancellationToken cancellationToken = default)
    {
        // Closing a position is the auto-flatten primitive (R-13); ungranted until the execution slice, so refuse at
        // the seam rather than leave a caller believing a flatten path exists here.
        Capabilities.Require(VenueCapability.ClosePosition);
        throw new UnreachableException();
    }
}
