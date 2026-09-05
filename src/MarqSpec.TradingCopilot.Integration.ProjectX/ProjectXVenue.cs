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
/// detail stops here — the two SignalR hubs, the success-flag error convention, integer account ids, and the
/// gateway's <c>simulated</c> execution-routing flag — so the core sees only the venue-neutral model.
/// The flag is <b>not</b> read as practice-vs-live: that is the operator's declaration, not the gateway's
/// (<see cref="TradingMode"/>, gh#60).
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
    private readonly FirmConventions _conventions;
    private readonly bool _live;

    /// <summary>Creates the adapter over a configured gateway client.</summary>
    /// <param name="api">The gateway's REST client.</param>
    /// <param name="webSocket">The gateway's realtime client.</param>
    /// <param name="dataTier">
    /// Which market-data universe these credentials are entitled to. Required rather than defaulted: the wrong
    /// tier returns an <b>empty</b> contract universe rather than an error, so a silent default would surface
    /// much later as an unresolvable instrument.
    /// </param>
    /// <param name="conventions">
    /// What the firm behind this login has declared each stage to mean (R-14, gh#60). One login is one firm
    /// (ADR-0016), so a single set of conventions scopes the adapter. <see cref="FirmConventions.None"/> is
    /// explicit for "nothing declared yet" — every account then resolves to <see cref="TradingMode.Undeclared"/>
    /// and is tradeable nowhere, which is the intended failure direction, not an accident.
    /// </param>
    public ProjectXVenue(
        IProjectXApiClient api,
        IProjectXWebSocketClient webSocket,
        ProjectXDataTier dataTier,
        FirmConventions conventions)
    {
        _api = api;
        _webSocket = webSocket;
        _conventions = conventions;
        _live = dataTier switch
        {
            ProjectXDataTier.Simulated => false,
            ProjectXDataTier.Live => true,

            // An unrecognized tier must not fall through to simulated: that would silently recreate
            // the empty-universe failure this parameter exists to prevent.
            _ => throw new ArgumentOutOfRangeException(
                nameof(dataTier), dataTier, "Unrecognized ProjectX market-data tier."),
        };
    }

    /// <inheritdoc />
    public VenueId Id { get; } = VenueId.Parse("projectx");

    /// <summary>
    /// What this adapter can actually deliver <b>through <see cref="ITradingVenue"/></b>. The gateway itself does
    /// more — depth, trailing stops — but none of that is reachable through the venue-neutral contract yet, and
    /// advertising it would defeat the purpose of the capability model: a caller checking before it commits would
    /// pick a path that cannot work.
    /// </summary>
    public VenueCapabilities Capabilities { get; } = VenueCapabilities.Of(
        VenueCapability.HistoricalBars
        | VenueCapability.Quotes
        | VenueCapability.ClosePosition
        // The always-native safety stop rides the entry as a stop-loss bracket (gh#11 inc 3): the gateway holds
        // it and attaches it on fill, so the neutral contract genuinely reaches this capability now.
        | VenueCapability.BracketOrders
        // Order / position / fill events over the user hub, behind the IAccountEventStream seam (gh#219): the
        // neutral contract now reaches them, so an order's terminal status and its fills are no longer invisible.
        | VenueCapability.AccountStreaming
        // In-place order modify (gh#259): the gateway's modify endpoint, now reached through the neutral contract
        // so an operator can reprice a resting working order without a cancel/replace.
        | VenueCapability.ModifyOrder
        );

    /// <summary>
    /// The ProjectX derivation-logic version (ADR-0009, gh#9). History: <b>1</b> — the conservative PRAC-only
    /// stage resolver (gh#86); <b>2</b> — the roster-grounded name families (gh#94). Bump on any change to how
    /// raw gateway facts become derived values (stage, mode); the pinning unit test makes the bump a
    /// deliberate, reviewed act.
    /// </summary>
    public int AdapterLogicVersion => 2;

    /// <inheritdoc />
    public async Task<ResolvedContract> ResolveContractAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken = default)
    {
        List<ClientModels.Contract> matches =
            [.. await _api.SearchContractsAsync(instrument.Symbol, _live, cancellationToken)];

        // Prefer the front month the gateway marks active; a search can also return expired or back months.
        ClientModels.Contract? contract = matches.Find(c => c.ActiveContract) ?? matches.FirstOrDefault();

        // The instrument travels with the handle: this method is the only place that knows they belong together.
        return contract is null
            ? throw new ProjectXVenueException($"No ProjectX contract matches instrument '{instrument}'.")
            : new ResolvedContract(VenueContractId.Create(Id, contract.Id), instrument);
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

        // Stage comes from the account name (conservatively -- ProjectXAccountStage refuses to guess), and what
        // that stage *means* comes from the firm's declared conventions. An unrecognised name or an undeclared
        // stage lands on Undeclared: "classify this before trading it" beats "assumed practice, then traded a
        // funded account" (gh#60). The venue's own `simulated` flag is deliberately not consulted.
        return
        [
            .. accounts.Select(account => ProjectXMapping.ToVenueAccount(
                account, Id, _conventions, ProjectXAccountStage.Resolve(account.Name))),
        ];
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
            // The client-supplied correlation handle (gh#577): the gateway echoes it on the order and on the
            // resting-orders read, so a replay can recognise its own already-placed order. Null unless the source set one.
            CustomTag = request.CustomTag,
            // The always-native safety stop (ADR-0007, gh#11 inc 3): a stop-loss bracket the gateway holds and
            // attaches on fill, so the position is never unprotected. A stop-type bracket at the safety price.
            StopLossBracket = request.ProtectiveStop is { } protectiveStop
                ? new ClientModels.OrderBracket
                {
                    Type = ProjectXMapping.ToClientType(OrderType.Stop),
                    StopPrice = protectiveStop.Value,
                }
                : null,
            // The take-profit leg (ADR-0007, gh#170): the profit side of the OCO, a limit-type bracket at the
            // target price the gateway holds and attaches on fill. Absent for a stop-only (two-leg) bracket.
            TakeProfitBracket = request.ProfitTarget is { } profitTarget
                ? new ClientModels.OrderBracket
                {
                    Type = ProjectXMapping.ToClientType(OrderType.Limit),
                    LimitPrice = profitTarget.Value,
                }
                : null,
        };

        ClientModels.PlaceOrderResponse response = await _api.PlaceOrderAsync(payload, cancellationToken);

        if (!response.Success)
        {
            // DEFINITIVE (gh#629): the gateway responded in the negative and placed nothing, so the caller can
            // auto-resolve the row. Classified HERE, where !success is unambiguous -- never inferred at the catch.
            throw new VenueRefusalException(
                $"ProjectX rejected the order: {response.ErrorMessage ?? "no reason given"}.",
                VenueRefusalKind.Definitive,
                response.ErrorCode);
        }

        if (response.OrderId is not { } orderId)
        {
            // INDETERMINATE (gh#629): accepted but unidentified -- the venue took it, so an order MAY be resting with
            // no handle to cancel or flatten against. The caller must keep the durable intent, never assume absence.
            throw new VenueRefusalException(
                "ProjectX accepted the order but returned no order id.", VenueRefusalKind.Indeterminate);
        }

        return new PlacedOrder(
            request.Account,
            orderId.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow)
        {
            // The correlation handle this order carries (gh#577) — the value we sent; the gateway's place-response
            // does not re-report it, and echoing our own key keeps the acknowledgement self-describing.
            CustomTag = request.CustomTag,
        };
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
    public async Task ModifyOrderAsync(
        VenueAccountId account,
        string venueOrderId,
        Price? limitPrice,
        Price? stopPrice,
        int? size,
        CancellationToken cancellationToken = default)
    {
        // Fail-closed at the seam: a caller that reached here without the capability is refused loudly rather than
        // sending a malformed request the gateway would reject obscurely (R-17).
        Capabilities.Require(VenueCapability.ModifyOrder);

        // Only the fields the caller means to change are sent; a null leaves the gateway's current value untouched.
        // Ids go through the venue-qualifier guards (ToAccountId / ToOrderId) -- never parsed inline -- so a foreign
        // handle cannot reach ProjectX on a colliding key.
        ClientModels.ModifyOrderResponse response = await _api.ModifyOrderAsync(
            new ClientModels.ModifyOrderRequest
            {
                AccountId = ProjectXMapping.ToAccountId(account, Id),
                OrderId = ProjectXMapping.ToOrderId(venueOrderId),
                LimitPrice = limitPrice?.Value,
                StopPrice = stopPrice?.Value,
                Size = size,
            },
            cancellationToken);

        if (!response.Success)
        {
            throw new ProjectXVenueException(
                $"ProjectX refused to modify order {venueOrderId}: {response.ErrorMessage ?? "no reason given"}.",
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

    /// <inheritdoc />
    public async Task<PositionSnapshot> ReducePositionAsync(
        VenueAccountId account,
        VenueContractId contract,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new NotImplementedException("gh#928: the sized partial close is not implemented yet.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkingOrder>> GetWorkingOrdersAsync(
        VenueAccountId account,
        CancellationToken cancellationToken = default)
    {
        // The gateway's open-orders truth (gh#183): OCO-cancel-on-exit reads this to find the protective legs still
        // resting on a now-flat contract. The gateway returns no parent/OCO linkage, so the caller decides which
        // legs are dangling by matching against the journaled entries, not a field here.
        IEnumerable<ClientModels.Order> orders =
            await _api.GetOpenOrdersAsync(ProjectXMapping.ToAccountId(account, Id), cancellationToken);

        return [.. orders.Select(order => ProjectXMapping.ToWorkingOrder(order, Id))];
    }

    /// <inheritdoc />
    public async Task<TaggedFillEvidence> FindFilledOrderByTagAsync(
        VenueAccountId account,
        string customTag,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customTag);

        // The gateway's ORDER HISTORY, not its open orders (gh#631): a filled order has left the open book, so the
        // resting read above cannot see it. The window starts at the caller's instant (the stranded row's own
        // creation) and is left open-ended, because an order can fill well after it was placed.
        //
        // Window field names confirmed against the gateway swagger and corrected (gh#642): the submodule's
        // SearchOrderRequest now serialises startTimestamp / endTimestamp — the names /api/Order/search requires
        // (and startTimestamp is mandatory). It previously sent startTime / endTime, which the gateway dropped, so
        // this search returned nothing and the veto silently degraded to the pre-gh#631 behaviour — fail-safe (an
        // empty history reads as NoFillFound, authorising nothing) but shipped-yet-inert until the field-name fix.
        IEnumerable<ClientModels.Order> history = await _api.GetOrdersAsync(
            ProjectXMapping.ToAccountId(account, Id),
            since.UtcDateTime,
            endTime: null,
            cancellationToken);

        // Match on the tag we stamped, and require an EXECUTED quantity. A partial fill counts: any executed size
        // means the order reached the market and did something, which is exactly what the caller needs to know.
        //
        // ORDERED, deliberately. The tag is a CORRELATION handle, not a venue idempotency key -- a re-attempt after a
        // transport fault stamps the same tag again -- so two executed records under one tag is possible. The gateway
        // documents no ordering, and picking whichever the JSON array happened to list first would journal an
        // arbitrary one of them. Take the EARLIEST execution: it is the entry that actually opened the position, and
        // it is stable across calls. (Which one is chosen never changes the veto itself -- any match vetoes.)
        List<ClientModels.Order> matches = [.. history
            .Where(order => string.Equals(order.CustomTag, customTag, StringComparison.Ordinal) && order.FillVolume > 0)
            .OrderBy(order => order.CreationTimestamp)
            .ThenBy(order => order.Id)];

        ClientModels.Order? filled = matches.FirstOrDefault();

        // REPORT the count, do not just silently take the earliest (PR #637 review). Choosing the earliest keeps the
        // veto correct — any match vetoes, so the row is never released either way — but the evidence below describes
        // only that one record, so a caller journaling from it UNDERSTATES the venue: a re-transmit that round-tripped
        // twice leaves two executions and journals one. That narrows the R-8/R-9 discard this feature exists to stop,
        // from "the whole fill" to "the second fill". Narrower, but still silent unless someone is told — so the count
        // rides on the evidence and FillReconciliationService, which has the logger, raises it.

        // Absence is reported as NoFillFound rather than Unavailable ONLY because the call itself succeeded. Note
        // that the caller treats the two identically for the purpose of releasing a row -- neither authorises
        // anything (see TaggedFillEvidence) -- so being wrong in this direction cannot place a second order. Any
        // failure of this call surfaces as an exception and is mapped to Unavailable by the calling service.
        if (filled is null)
        {
            return TaggedFillEvidence.NoFillFound(customTag);
        }

        TaggedFillEvidence evidence = TaggedFillEvidence.Filled(
            customTag,
            filled.FillVolume,
            filled.FilledPrice,
            filled.Id.ToString(CultureInfo.InvariantCulture),
            matches.Count);

        return evidence.WithLegs(await ReadFillLegsAsync(account, filled.Id, since, cancellationToken));
    }

    /// <summary>
    /// The executed fills behind a matched order (gh#770), so an adopt can backfill the <c>Fill</c> rows that were
    /// dropped while the row was stranded without a venue key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>second, journalling-only</b> read, and it is deliberately best-effort: the veto above protects a live
    /// account, so it must never regress because this failed. A fault here costs the backfill and returns no legs,
    /// leaving exactly the pre-gh#770 answer — the caller still refuses to release the row.
    /// </para>
    /// <para>
    /// Each leg carries the venue's <b>own</b> fill id, which is the same key the account-event stream writes. The
    /// backfilled rows are therefore indistinguishable from streamed ones, and <c>Fill</c>'s
    /// <c>(OrderId, VenueFillKey)</c> unique index makes a later delivery or replay an idempotent no-op by
    /// construction rather than a double-counted entry.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<TaggedFillLeg>> ReadFillLegsAsync(
        VenueAccountId account,
        long venueOrderId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        IEnumerable<ClientModels.HalfTrade> trades;
        try
        {
            trades = await _api.GetTradesAsync(
                ProjectXMapping.ToAccountId(account, Id), since.UtcDateTime, endTime: null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Swallowed, and the EMPTY leg list is the signal. This venue carries no logger by design, and the
            // established split in this file is that FillReconciliationService -- which does -- raises what the
            // evidence carries (the same way it raises the MatchCount ambiguity above). Reporting here would
            // duplicate that, and throwing would turn a journalling-only fault into a lost veto.
            return [];
        }

        // Scoped to THIS order: the search is account-and-window wide, so another order's executions are in the
        // response too, and attaching them would journal fills this order never made. Voided trades are skipped
        // exactly as the ingestion path skips them -- a retracted execution must never reach the journal.
        return
        [
            .. trades
                .Where(trade => trade.OrderId == venueOrderId && !trade.Voided && trade.Size > 0)
                .OrderBy(trade => trade.CreationTimestamp)
                .ThenBy(trade => trade.Id)
                .Select(trade => new TaggedFillLeg(
                    trade.Id.ToString(CultureInfo.InvariantCulture),
                    trade.Price,
                    trade.Size,
                    trade.Fees,
                    new DateTimeOffset(DateTime.SpecifyKind(trade.CreationTimestamp, DateTimeKind.Utc)))),
        ];
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

        // The realtime feed tags quotes by PRODUCT ROOT (F.US.EP), not the full contract id we subscribed with
        // (CON.F.US.EP.M25) -- so filter on the root, or every tick is dropped (gh#163).
        string quoteSymbol = ProjectXMapping.ToQuoteSymbol(contractKey);

        void OnPriceUpdate(object? sender, ClientModels.PriceUpdate update)
        {
            // One hub carries every subscribed contract, so updates are filtered back down to this one.
            if (string.Equals(update.Symbol, quoteSymbol, StringComparison.Ordinal))
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
