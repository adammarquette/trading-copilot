using System.Globalization;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.Integration.ProjectX;

/// <summary>
/// Translates between the ProjectX gateway's vocabulary and the venue-neutral model. Everything venue-specific
/// about ProjectX — unsigned position sizes with a separate direction, value-per-tick instead of value-per-point,
/// integer account ids — is absorbed here so none of it reaches the core (R-17).
/// </summary>
public static class ProjectXMapping
{
    private const long SecondsPerMinute = 60;
    private const long SecondsPerHour = 3_600;
    private const long SecondsPerDay = 86_400;

    /// <summary>Maps a gateway trading account onto the venue-neutral account.</summary>
    /// <param name="account">The gateway's account.</param>
    /// <param name="venue">The venue to tag it with.</param>
    /// <param name="conventions">What the firm holding this account has declared each stage to mean.</param>
    /// <param name="stage">Which stage this account sits at.</param>
    /// <returns>The venue-neutral account.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="TradingMode"/> is resolved through <paramref name="conventions"/>, which decide whether the
    /// gateway's <c>simulated</c> flag may be consulted at all — this method <b>passes</b> the flag and never
    /// applies it. At a <b>prop firm</b> it is ignored: that flag says where an order executes, which on a prop
    /// platform is close to orthogonal to what is at stake — a funded account reports <c>simulated=true</c> and
    /// executes on a simulated engine, yet a breach costs a real payout. Reading it as economic stake classified
    /// exactly the account that matters most as harmless (gh#60).
    /// </para>
    /// <para>
    /// At a <b>brokerage</b> the same flag <i>is</i> the answer (gh#780), because there a paper account is
    /// practice and a live account is live. It is also carried onto the returned account raw, so the later
    /// recomputes of the persisted mode — which have no venue call of their own — see the same input this one did.
    /// </para>
    /// <para>
    /// Both inputs are required rather than defaulted. An account whose stage is unknown, or whose firm has
    /// declared nothing, comes back <see cref="TradingMode.Undeclared"/> and is tradeable nowhere (R-14).
    /// </para>
    /// </remarks>
    public static VenueAccount ToVenueAccount(
        ClientModels.TradingAccount account,
        VenueId venue,
        FirmConventions conventions,
        AccountStage stage)
    {
        return new VenueAccount(
            VenueAccountId.Create(venue, account.Id.ToString(CultureInfo.InvariantCulture)),
            account.Name,
            account.Balance,
            account.CanTrade,
            account.IsVisible,
            // The venue's own routing flag is passed in, not applied: FirmConventions decides whether it may be
            // read at all. At a prop firm it is ignored (a funded account reports `simulated` and is Live, R-14);
            // at a brokerage it IS the answer (gh#780).
            conventions.ModeFor(stage, account.Simulated))
        {
            Stage = stage,
            // Carried raw so the LATER recomputes -- a stage override, a conventions re-declaration -- have the
            // same input this one had. Those write points have no live venue call to ask again (gh#780).
            VenueReportsSimulated = account.Simulated,
        };
    }

    /// <summary>Maps a gateway contract onto the instrument spec the money math needs.</summary>
    /// <param name="contract">The gateway's contract.</param>
    /// <param name="instrument">The venue-neutral instrument the contract represents.</param>
    /// <returns>The instrument spec.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The contract has no usable tick size.</exception>
    /// <remarks>
    /// ProjectX publishes <c>tickValue</c> (money per tick); the domain works in money per <i>point</i>, so the
    /// two are divided. ES: $12.50 a tick at a 0.25 tick size is $50 a point.
    /// </remarks>
    public static InstrumentSpec ToInstrumentSpec(ClientModels.Contract contract, InstrumentId instrument)
    {
        if (contract.TickSize <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contract), contract.TickSize, "The contract's tick size must be positive.");
        }

        return InstrumentSpec.Create(instrument, contract.TickSize, contract.TickValue / contract.TickSize);
    }

    /// <summary>Maps a gateway position onto the venue-neutral snapshot.</summary>
    /// <param name="position">The gateway's position.</param>
    /// <param name="venue">The venue to tag it with.</param>
    /// <returns>The position snapshot, with a signed quantity.</returns>
    /// <remarks>
    /// The gateway reports an <b>unsigned</b> size alongside a direction enum. Losing that sign would invert the
    /// position for every downstream risk and flatten decision, so it is applied here.
    /// </remarks>
    public static PositionSnapshot ToPositionSnapshot(ClientModels.Position position, VenueId venue)
    {
        int netQuantity = position.Type switch
        {
            ClientModels.PositionType.Long => position.Size,
            ClientModels.PositionType.Short => -position.Size,

            // A directionless position with no size is genuinely flat.
            _ when position.Size == 0 => 0,

            // Anything else is exposure we cannot describe. Reporting it as flat would tell the risk gate and
            // auto-flatten there is nothing open -- fail loudly instead.
            _ => throw new ProjectXVenueException(
                $"ProjectX reported {position.Size} contracts on {position.ContractId} with direction "
                + $"'{position.Type}', which cannot be mapped to a signed exposure."),
        };

        return new PositionSnapshot(
            VenueAccountId.Create(venue, position.AccountId.ToString(CultureInfo.InvariantCulture)),
            VenueContractId.Create(venue, position.ContractId),
            netQuantity,
            new Price(position.AveragePrice));
    }

    /// <summary>Maps a gateway bar onto the venue-neutral bar.</summary>
    /// <param name="bar">The gateway's aggregate bar.</param>
    /// <returns>The venue-neutral bar.</returns>
    public static Bar ToBar(ClientModels.AggregateBar bar)
    {
        return new Bar(
            ToUtc(bar.Timestamp),
            new Price(bar.Open),
            new Price(bar.High),
            new Price(bar.Low),
            new Price(bar.Close),
            bar.Volume);
    }

    /// <summary>Maps a gateway price update onto the venue-neutral quote.</summary>
    /// <param name="update">The gateway's price update.</param>
    /// <returns>The quote, with resting sizes absent.</returns>
    /// <remarks>
    /// ProjectX's quote stream carries prices only — resting size arrives on the depth stream — so the sizes are
    /// reported as absent rather than as a misleading zero.
    /// </remarks>
    public static Quote ToQuote(ClientModels.PriceUpdate update)
    {
        return new Quote(
            ToUtc(update.Timestamp),
            new Price(update.BestBid),
            new Price(update.BestAsk),
            BidSize: null,
            AskSize: null);
    }

    /// <summary>Parses the venue-neutral account key back into the integer id the gateway expects.</summary>
    /// <param name="account">The venue-qualified account.</param>
    /// <param name="venue">This adapter's venue — the account must belong to it.</param>
    /// <returns>The gateway's account id.</returns>
    /// <exception cref="ArgumentException">
    /// The account belongs to another venue, or its key is not a ProjectX account number.
    /// </exception>
    /// <remarks>
    /// The qualifier is checked, not assumed. Account handles are bare integers that collide freely across
    /// venues, so a <c>tradovate:9001</c> reaching this adapter would otherwise be sent to <i>ProjectX</i>
    /// account 9001 — a different, possibly real-money account (R-17).
    /// </remarks>
    public static int ToAccountId(VenueAccountId account, VenueId venue)
    {
        EnsureBelongsTo(account.Venue, venue, account.ToString());

        return int.TryParse(account.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            ? id
            : throw new ArgumentException($"'{account.Key}' is not a ProjectX account number.", nameof(account));
    }

    /// <summary>Unwraps a venue-qualified contract to the bare handle the gateway expects.</summary>
    /// <param name="contract">The venue-qualified contract.</param>
    /// <param name="venue">This adapter's venue — the contract must belong to it.</param>
    /// <returns>The gateway's contract handle.</returns>
    /// <exception cref="ArgumentException">The contract belongs to another venue.</exception>
    /// <remarks>
    /// Every path that strips the qualifier goes through here, so a foreign contract can never reach the gateway
    /// on a colliding key — which on the flatten path would mean closing the wrong position.
    /// </remarks>
    public static string ToContractKey(VenueContractId contract, VenueId venue)
    {
        EnsureBelongsTo(contract.Venue, venue, contract.ToString());

        return contract.Key;
    }

    /// <summary>
    /// The <b>product root</b> the gateway tags <c>GatewayQuote</c> updates with, derived from a full contract
    /// id (gh#163). Subscription uses the full id (<c>CON.F.US.EP.M25</c>) but the realtime feed reports quotes
    /// by root (<c>F.US.EP</c>) — so a stream filtered on the full id drops every tick. Strips the <c>CON.</c>
    /// prefix and the trailing expiry segment.
    /// </summary>
    /// <param name="contractKey">The full contract id (e.g. <c>CON.F.US.EP.M25</c>).</param>
    /// <returns>The product root (e.g. <c>F.US.EP</c>).</returns>
    public static string ToQuoteSymbol(string contractKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractKey);

        string[] segments = contractKey.Split('.');

        // Drop a leading "CON" wrapper and the trailing expiry segment; the product root is what remains.
        int start = segments.Length > 0 && segments[0] == "CON" ? 1 : 0;
        int end = segments.Length - 1; // exclusive: the last segment is the expiry (e.g. M25)

        return end > start ? string.Join('.', segments[start..end]) : contractKey;
    }

    private static void EnsureBelongsTo(VenueId actual, VenueId expected, string qualified)
    {
        if (actual != expected)
        {
            throw new ArgumentException(
                $"'{qualified}' belongs to venue '{actual}', not '{expected}'.", nameof(qualified));
        }
    }

    /// <summary>Parses a venue order handle back into the integer id the gateway expects.</summary>
    /// <param name="venueOrderId">The order handle from <see cref="PlacedOrder.VenueOrderId"/>.</param>
    /// <returns>The gateway's order id.</returns>
    /// <exception cref="ArgumentException">The handle is not a ProjectX order id.</exception>
    public static long ToOrderId(string venueOrderId)
    {
        return long.TryParse(venueOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
            ? id
            : throw new ArgumentException($"'{venueOrderId}' is not a ProjectX order id.", nameof(venueOrderId));
    }

    /// <summary>Maps the venue-neutral side onto the gateway's bid/ask vocabulary.</summary>
    /// <param name="side">Buy or sell.</param>
    /// <returns>The gateway's side.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The side is not recognized.</exception>
    public static ClientModels.OrderSide ToClientSide(OrderSide side)
    {
        return side switch
        {
            OrderSide.Buy => ClientModels.OrderSide.Bid,
            OrderSide.Sell => ClientModels.OrderSide.Ask,
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Unrecognized order side."),
        };
    }

    /// <summary>Maps the venue-neutral order type onto the gateway's.</summary>
    /// <param name="type">The venue-neutral order type.</param>
    /// <returns>The gateway's order type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The type is not recognized.</exception>
    public static ClientModels.OrderType ToClientType(OrderType type)
    {
        return type switch
        {
            OrderType.Market => ClientModels.OrderType.Market,
            OrderType.Limit => ClientModels.OrderType.Limit,
            OrderType.Stop => ClientModels.OrderType.Stop,
            OrderType.StopLimit => ClientModels.OrderType.StopLimit,
            OrderType.TrailingStop => ClientModels.OrderType.TrailingStop,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unrecognized order type."),
        };
    }

    /// <summary>Maps the gateway's bid/ask side onto the venue-neutral buy/sell (gh#219).</summary>
    /// <param name="side">The gateway's side.</param>
    /// <returns>Buy or sell.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The side is not recognized.</exception>
    public static OrderSide ToVenueSide(ClientModels.OrderSide side)
    {
        return side switch
        {
            ClientModels.OrderSide.Bid => OrderSide.Buy,
            ClientModels.OrderSide.Ask => OrderSide.Sell,
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Unrecognized gateway order side."),
        };
    }

    /// <summary>Maps the gateway's order status onto the venue-neutral order state (gh#219).</summary>
    /// <param name="status">The gateway's order status.</param>
    /// <returns>The neutral state; a status the adapter cannot map becomes <see cref="VenueOrderState.Unknown"/>.</returns>
    public static VenueOrderState ToVenueOrderState(ClientModels.OrderStatus status)
    {
        return status switch
        {
            ClientModels.OrderStatus.Open => VenueOrderState.Working,
            ClientModels.OrderStatus.Filled => VenueOrderState.Filled,
            ClientModels.OrderStatus.Cancelled => VenueOrderState.Cancelled,
            ClientModels.OrderStatus.Expired => VenueOrderState.Expired,
            ClientModels.OrderStatus.Rejected => VenueOrderState.Rejected,
            ClientModels.OrderStatus.Pending => VenueOrderState.Pending,

            // None, or a status a newer gateway adds, must not read as a real state -- fail closed (gh#60).
            _ => VenueOrderState.Unknown,
        };
    }

    /// <summary>Maps a gateway order-update onto the neutral <see cref="OrderStateEvent"/> (gh#219).</summary>
    /// <param name="update">The gateway's order update.</param>
    /// <param name="venue">The venue to tag the event with.</param>
    /// <returns>The neutral order-state event.</returns>
    public static OrderStateEvent ToOrderStateEvent(ClientModels.OrderUpdate update, VenueId venue)
    {
        ArgumentNullException.ThrowIfNull(update);

        return new OrderStateEvent(
            VenueAccountId.Create(venue, update.AccountId.ToString(CultureInfo.InvariantCulture)),
            ToUtc(update.UpdateTimestamp ?? update.CreationTimestamp),
            update.Id.ToString(CultureInfo.InvariantCulture),
            ToVenueOrderState(update.Status),
            update.FillVolume,
            update.FilledPrice is { } filledPrice ? new Price(filledPrice) : null);
    }

    /// <summary>Maps a gateway trade notification onto the neutral <see cref="FillEvent"/> (gh#219).</summary>
    /// <param name="trade">The gateway's trade notification.</param>
    /// <param name="venue">The venue to tag the event with.</param>
    /// <returns>The neutral fill event; the gateway trade id is its idempotency key.</returns>
    public static FillEvent ToFillEvent(ClientModels.TradeNotification trade, VenueId venue)
    {
        ArgumentNullException.ThrowIfNull(trade);

        return new FillEvent(
            VenueAccountId.Create(venue, trade.AccountId.ToString(CultureInfo.InvariantCulture)),
            ToUtc(trade.CreationTimestamp),
            trade.OrderId.ToString(CultureInfo.InvariantCulture),
            trade.Id.ToString(CultureInfo.InvariantCulture),
            ToVenueSide(trade.Side),
            trade.Size,
            new Price(trade.Price),
            trade.Fees,
            trade.Voided);
    }

    /// <summary>Maps a gateway position-update onto the neutral <see cref="PositionEvent"/> (gh#219).</summary>
    /// <param name="position">The gateway's position update.</param>
    /// <param name="venue">The venue to tag the event with.</param>
    /// <returns>The neutral position event, with a signed net exposure.</returns>
    public static PositionEvent ToPositionEvent(ClientModels.PositionUpdate position, VenueId venue)
    {
        ArgumentNullException.ThrowIfNull(position);

        int netQuantity = position.Type switch
        {
            ClientModels.PositionType.Long => position.Size,
            ClientModels.PositionType.Short => -position.Size,
            _ => 0,
        };

        return new PositionEvent(
            VenueAccountId.Create(venue, position.AccountId.ToString(CultureInfo.InvariantCulture)),
            ToUtc(position.CreationTimestamp),
            VenueContractId.Create(venue, position.ContractId),
            netQuantity,
            new Price(position.AveragePrice));
    }

    /// <summary>Maps a gateway open order onto the venue-neutral <see cref="WorkingOrder"/> (gh#183).</summary>
    /// <param name="order">The gateway's order (from an open-orders query).</param>
    /// <param name="venue">The venue to tag it with.</param>
    /// <returns>The neutral working-order view — the handle, contract, resting price, and size.</returns>
    public static WorkingOrder ToWorkingOrder(ClientModels.Order order, VenueId venue)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new WorkingOrder(
            order.Id.ToString(CultureInfo.InvariantCulture),
            VenueContractId.Create(venue, order.ContractId),
            order.StopPrice is { } stopPrice ? new Price(stopPrice) : null,
            order.LimitPrice is { } limitPrice ? new Price(limitPrice) : null,

            // The gateway has always carried this; the projection dropped it (gh#381). Without it, a protective
            // leg sized to LESS than the position it guards is invisible -- and a partially-covered position is
            // not a protected one.
            order.Size)
        {
            // The client correlation handle the placing request stamped (gh#577), echoed back by the gateway — how a
            // replay matches its own already-placed order (a conditional left live by a transmit→journal fault).
            CustomTag = order.CustomTag,
        };
    }

    /// <summary>Expresses a bar duration as the gateway's unit plus a count.</summary>
    /// <param name="barSize">The bar duration.</param>
    /// <returns>The gateway's unit and unit count.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The duration is not a positive whole number of seconds.</exception>
    public static (ClientModels.AggregateBarUnit Unit, int Number) ToBarUnit(TimeSpan barSize)
    {
        if (barSize <= TimeSpan.Zero || barSize.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(barSize), barSize, "A bar size must be a positive whole number of seconds.");
        }

        long seconds = barSize.Ticks / TimeSpan.TicksPerSecond;

        // Coarsest exact unit, so a 5-minute bar is requested as 5 minutes rather than 300 seconds.
        return seconds % SecondsPerDay == 0
            ? (ClientModels.AggregateBarUnit.Day, (int)(seconds / SecondsPerDay))
            : seconds % SecondsPerHour == 0
                ? (ClientModels.AggregateBarUnit.Hour, (int)(seconds / SecondsPerHour))
                : seconds % SecondsPerMinute == 0
                    ? (ClientModels.AggregateBarUnit.Minute, (int)(seconds / SecondsPerMinute))
                    : (ClientModels.AggregateBarUnit.Second, (int)seconds);
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        // The gateway's timestamps arrive without a kind; they are UTC, and guessing local here would shift every
        // bar and quote by the operator's offset.
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
