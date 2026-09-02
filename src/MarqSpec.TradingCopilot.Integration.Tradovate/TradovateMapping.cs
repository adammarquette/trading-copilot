using System.Globalization;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.Integration.Tradovate;

/// <summary>
/// Translates between the Tradovate client's vocabulary and the venue-neutral model. Everything venue-specific about
/// Tradovate — integer account / contract ids, an <b>already-signed</b> net position, and demo-vs-live as the mode
/// source (a brokerage, gh#780) — is absorbed here so none of it reaches the core (R-17).
/// </summary>
public static class TradovateMapping
{
    /// <summary>
    /// The cap on bars a single <see cref="ToChartRequest"/> asks for, as the ProjectX adapter pins (its <c>limit: 1000</c>).
    /// Without it Tradovate returns however many it chooses for the range, so a caller cannot tell a clipped series from
    /// a complete one; with it the far edge is truncated deterministically. Surfaced on <c>GetBarsAsync</c>.
    /// </summary>
    private const int MaxBars = 1000;

    /// <summary>Maps a Tradovate contract onto the venue-neutral resolved contract, paired with its instrument.</summary>
    /// <param name="contract">The Tradovate contract.</param>
    /// <param name="instrument">The instrument it was resolved for.</param>
    /// <param name="venue">The venue to tag the handle with.</param>
    /// <returns>The handle paired with its instrument.</returns>
    /// <exception cref="TradovateVenueException">The contract has no id.</exception>
    /// <remarks>
    /// The pairing (handle + instrument) is the venue's to make — carrying it forward is what lets the execution path
    /// catch a proposal sized for one instrument sent as another's contract (<see cref="ResolvedContract"/>).
    /// </remarks>
    public static ResolvedContract ToResolvedContract(
        ClientModels.Contract contract,
        InstrumentId instrument,
        VenueId venue)
    {
        ArgumentNullException.ThrowIfNull(contract);

        return contract.Id is { } id
            ? new ResolvedContract(VenueContractId.Create(venue, id.ToString(CultureInfo.InvariantCulture)), instrument)
            : throw new TradovateVenueException($"Tradovate returned a contract for '{instrument}' with no id.");
    }

    /// <summary>Maps a Tradovate position onto the venue-neutral snapshot, keeping the sign.</summary>
    /// <param name="position">The Tradovate position.</param>
    /// <param name="venue">The venue to tag it with.</param>
    /// <returns>The snapshot, with a signed net quantity.</returns>
    /// <remarks>
    /// Tradovate's <c>netPos</c> is <b>already signed</b> (long positive, short negative) — unlike ProjectX's unsigned
    /// size plus a separate direction — so it maps straight through with no chance of an inverted exposure. A flat
    /// position's price is immaterial; a held one carries <c>netPrice</c>.
    /// </remarks>
    public static PositionSnapshot ToPositionSnapshot(ClientModels.Position position, VenueId venue)
    {
        ArgumentNullException.ThrowIfNull(position);

        // A held position must carry its average entry price. `netPrice` is nullable on the wire and absent ≠ zero
        // (the client contract) — fabricating a 0 basis for an open position would feed a wildly wrong unrealised
        // P&L to any risk / P&L consumer, so refuse it loudly rather than invent a price. A flat position (netPos 0)
        // is filtered out by the caller before this; its price is immaterial, so the fallback below is unreached there.
        if (position.NetPos != 0 && position.NetPrice is null)
        {
            throw new TradovateVenueException(
                $"Tradovate reported an open position ({position.NetPos} on contract {position.ContractId}) with no "
                + "net price, which cannot be mapped to an average entry price.");
        }

        return new PositionSnapshot(
            VenueAccountId.Create(venue, position.AccountId.ToString(CultureInfo.InvariantCulture)),
            VenueContractId.Create(venue, position.ContractId.ToString(CultureInfo.InvariantCulture)),
            position.NetPos,
            new Price(position.NetPrice ?? 0m));
    }

    /// <summary>Maps a Tradovate account, with its cash balance, onto the venue-neutral account.</summary>
    /// <param name="account">The Tradovate account.</param>
    /// <param name="balance">The account's cash balance, read separately and joined by the caller.</param>
    /// <param name="venue">The venue to tag it with.</param>
    /// <param name="conventions">The firm's conventions — for Tradovate, a brokerage's (gh#780).</param>
    /// <param name="venueReportsSimulated">
    /// Whether the venue's own host is demo/paper (<see langword="true"/>) or live (<see langword="false"/>).
    /// <b>Non-nullable by design</b>: the caller must resolve the host <em>before</em> mapping and refuse an
    /// unrecognised one — see <see cref="IsSimulatedHost"/>. An account whose host could not be classified must not
    /// be mapped at all, because coercing an unknown flag to <see langword="false"/> here would let it persist as
    /// <see cref="TradingMode.Live"/> once the recompute reads the stored flag (the fail-open this signature prevents).
    /// </param>
    /// <returns>The venue-neutral account.</returns>
    /// <exception cref="TradovateVenueException">The account has no id.</exception>
    /// <remarks>
    /// <see cref="TradingMode"/> is resolved through <paramref name="conventions"/>. Tradovate is a brokerage
    /// (gh#780), so its conventions are <see cref="FirmConventions.ForBrokerage"/> and mode follows the venue's own
    /// host: a demo host is <see cref="TradingMode.Practice"/>, a live host is <see cref="TradingMode.Live"/>. The
    /// stage is always <see cref="AccountStage.Unknown"/>: a brokerage has no evaluation / funded ladder in a name.
    /// </remarks>
    public static VenueAccount ToVenueAccount(
        ClientModels.Account account,
        decimal balance,
        VenueId venue,
        FirmConventions conventions,
        bool venueReportsSimulated)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(conventions);

        return account.Id is { } id
            ? new VenueAccount(
                VenueAccountId.Create(venue, id.ToString(CultureInfo.InvariantCulture)),
                account.Name,
                balance,
                // Active AND explicitly not read-only. CanTrade is the gate OrderExecutionService checks before it
                // places an order, so an UNKNOWN (null) Readonly must fail closed to not-tradable rather than be
                // assumed tradable — hence `== false`, not `!= true` (review #990). If Tradovate turns out to report
                // null for normal accounts, slice 3's real-account wiring (gh#977) revisits this with live data.
                CanTrade: account.Active && account.Readonly == false,
                // Tradovate carries no per-account visibility flag; visibility is the operator's local toggle, so a
                // discovered account starts visible and the operator hides what they do not want to see.
                IsVisible: true,
                conventions.ModeFor(AccountStage.Unknown, venueReportsSimulated))
            {
                Stage = AccountStage.Unknown,
                // A resolved true/false, never a coerced null: the caller has already refused an unclassifiable host,
                // so the raw flag persisted here cannot silently recompute to Live downstream.
                VenueReportsSimulated = venueReportsSimulated,
            }
            : throw new TradovateVenueException($"Tradovate returned account '{account.Name}' with no id.");
    }

    /// <summary>Classifies the configured host as Tradovate's demo (paper) host, live host, or neither.</summary>
    /// <param name="configuredHost">The client's configured REST host.</param>
    /// <returns>
    /// <see langword="true"/> for the demo host, <see langword="false"/> for the live host, and <see langword="null"/>
    /// for any unrecognised host — which resolves to <see cref="TradingMode.Undeclared"/>, so an unexpected host fails
    /// closed (tradeable nowhere) rather than defaulting an account to live- or practice-tradeable.
    /// </returns>
    public static bool? IsSimulatedHost(string configuredHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredHost);

        // Compare the URL's HOST component, not a substring of the whole URL: a path or query could otherwise spoof
        // the classification (e.g. a live host with `?ref=demo.tradovateapi.com`). An unparseable value, or any host
        // that is neither Tradovate's demo nor live host, resolves to null — the caller then refuses the account
        // rather than defaulting it to a real mode.
        if (!Uri.TryCreate(configuredHost, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (string.Equals(uri.Host, "demo.tradovateapi.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(uri.Host, "live.tradovateapi.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    /// <summary>Parses a venue-qualified account handle back into the integer id Tradovate expects.</summary>
    /// <param name="account">The venue-qualified account.</param>
    /// <param name="venue">This adapter's venue — the account must belong to it.</param>
    /// <returns>Tradovate's account id.</returns>
    /// <exception cref="ArgumentException">The account belongs to another venue, or its key is not a Tradovate id.</exception>
    /// <remarks>
    /// The qualifier is checked, not assumed: account handles are bare integers that collide freely across venues, so
    /// a <c>projectx:9001</c> reaching this adapter must never be sent to <i>Tradovate</i> account 9001 (R-17).
    /// </remarks>
    public static long ToAccountId(VenueAccountId account, VenueId venue)
    {
        EnsureBelongsTo(account.Venue, venue, account.ToString());

        return long.TryParse(account.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
            ? id
            : throw new ArgumentException($"'{account.Key}' is not a Tradovate account id.", nameof(account));
    }

    /// <summary>Parses a venue-qualified contract handle back into the integer id Tradovate expects.</summary>
    /// <param name="contract">The venue-qualified contract.</param>
    /// <param name="venue">This adapter's venue — the contract must belong to it.</param>
    /// <returns>Tradovate's numeric contract id.</returns>
    /// <exception cref="ArgumentException">The contract belongs to another venue, or its key is not a Tradovate contract id.</exception>
    /// <remarks>
    /// The qualifier is checked, not assumed: contract handles collide across venues, so a <c>projectx:</c> contract
    /// must never be sent to Tradovate on a colliding key (R-17).
    /// </remarks>
    public static long ToContractId(VenueContractId contract, VenueId venue)
    {
        EnsureBelongsTo(contract.Venue, venue, contract.ToString());

        return long.TryParse(contract.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
            ? id
            : throw new ArgumentException($"'{contract.Key}' is not a Tradovate contract id.", nameof(contract));
    }

    /// <summary>Builds a Tradovate chart request for a contract over a time range at a bar size.</summary>
    /// <param name="contract">The venue-qualified contract.</param>
    /// <param name="from">Start of the range, the oldest edge.</param>
    /// <param name="to">End of the range, the newest edge.</param>
    /// <param name="barSize">The bar duration.</param>
    /// <param name="venue">This adapter's venue — the contract must belong to it.</param>
    /// <returns>The chart request.</returns>
    /// <exception cref="ArgumentException">The contract belongs to another venue, or its key is not a Tradovate contract id.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The bar size is not a positive whole number of minutes.</exception>
    public static ClientModels.ChartRequest ToChartRequest(
        VenueContractId contract,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan barSize,
        VenueId venue)
    {
        long contractId = ToContractId(contract, venue);

        if (from > to)
        {
            // An inverted range would build a well-formed request with the edges swapped and come back empty, which
            // reads downstream as "no bars in this window" rather than "you asked for a nonsense window". Refuse it.
            throw new ArgumentException($"The bar range is inverted: from ({from:o}) is after to ({to:o}).", nameof(from));
        }

        (ClientModels.ChartUnderlyingType underlyingType, int elementSize) = ToChartUnit(barSize);

        return new ClientModels.ChartRequest
        {
            ContractId = contractId,
            UnderlyingType = underlyingType,
            ElementSize = elementSize,
            ClosestTimestamp = to,     // the newest edge of the range
            AsFarAsTimestamp = from,   // the oldest edge
            AsMuchAsElements = MaxBars, // an explicit cap, so a clipped series is not mistaken for a complete one
        };
    }

    /// <summary>Expresses a bar duration as Tradovate's chart underlying type plus an element size.</summary>
    /// <param name="barSize">The bar duration.</param>
    /// <returns>The underlying type and element size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The duration is not a positive whole number of minutes. Tradovate's time bars are minute and daily; there are no
    /// sub-minute time bars, and a tick bar is a count of ticks (not a duration), so it is not reachable from a <see cref="TimeSpan"/>.
    /// </exception>
    public static (ClientModels.ChartUnderlyingType UnderlyingType, int ElementSize) ToChartUnit(TimeSpan barSize)
    {
        if (barSize <= TimeSpan.Zero || barSize.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(barSize), barSize,
                "A Tradovate bar size must be a positive whole number of minutes (minute and daily bars only, no sub-minute time bars).");
        }

        long minutes = barSize.Ticks / TimeSpan.TicksPerMinute;
        const long MinutesPerDay = 24 * 60;

        // Coarsest exact unit: whole days as a daily bar, otherwise minutes (an hour is 60 minute-elements — Tradovate has no hour bar).
        return minutes % MinutesPerDay == 0
            ? (ClientModels.ChartUnderlyingType.DailyBar, (int)(minutes / MinutesPerDay))
            : (ClientModels.ChartUnderlyingType.MinuteBar, (int)minutes);
    }

    /// <summary>Maps a Tradovate chart bar onto the venue-neutral bar.</summary>
    /// <param name="bar">The Tradovate chart bar.</param>
    /// <returns>The venue-neutral bar.</returns>
    /// <remarks>
    /// The domain bar carries an integer volume; Tradovate's <c>volume</c> is nullable and the domain has no way to
    /// represent "absent", so an omitted volume becomes 0 (a whole-contract count for futures).
    /// </remarks>
    public static Bar ToBar(ClientModels.ChartBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);

        return new Bar(
            bar.Timestamp,
            new Price(bar.Open),
            new Price(bar.High),
            new Price(bar.Low),
            new Price(bar.Close),
            (long)(bar.Volume ?? 0m));
    }

    /// <summary>Builds the venue-neutral quote from a resolved best bid/ask — a running snapshot the adapter keeps.</summary>
    /// <param name="timestamp">When the quote last changed.</param>
    /// <param name="bid">The best bid price.</param>
    /// <param name="ask">The best ask price.</param>
    /// <param name="bidSize">Resting bid size, or <see langword="null"/> when the socket omitted it (absent ≠ zero).</param>
    /// <param name="askSize">Resting ask size, or <see langword="null"/>.</param>
    /// <returns>The venue-neutral quote.</returns>
    /// <remarks>
    /// It takes a resolved bid + ask rather than a client <c>Quote</c> because Tradovate's quote events are
    /// incremental (bid-only / ask-only / last-trade), so the complete snapshot the neutral <see cref="Quote"/> needs
    /// is assembled by the adapter across events, not carried on any single one.
    /// </remarks>
    public static Quote ToQuote(DateTimeOffset timestamp, decimal bid, decimal ask, long? bidSize, long? askSize)
    {
        return new Quote(timestamp, new Price(bid), new Price(ask), bidSize, askSize);
    }

    /// <summary>Maps Tradovate's <c>ordStatus</c> onto the venue-neutral order state (R-17, gh#977).</summary>
    /// <param name="status">Tradovate's order status.</param>
    /// <returns>The neutral state; anything that cannot be mapped safely becomes <see cref="VenueOrderState.Unknown"/>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Two statuses deliberately fail closed</b> rather than take the nearest-looking neutral state (gh#60).
    /// <c>Completed</c> is <b>not</b> read as <see cref="VenueOrderState.Filled"/>: whether Tradovate means "fully
    /// executed" or merely "finished" is not pinned from this side, and reading a finished-but-unfilled order as a
    /// fill would tell the journal an execution happened. <c>Suspended</c> is live-but-not-executable, which is no
    /// neutral state at all. Both are harmless as <see cref="VenueOrderState.Unknown"/>, because the consumer acts
    /// only on the terminal <i>non-fill</i> transitions and takes filled volume from the fill events.
    /// </para>
    /// <para>
    /// <c>PendingCancel</c> / <c>PendingReplace</c> map to <see cref="VenueOrderState.Working"/>, not
    /// <see cref="VenueOrderState.Pending"/>: the neutral <c>Pending</c> means "submitted, not yet working", whereas
    /// an order awaiting an amendment is <b>still resting and still fillable</b>. Calling it not-yet-working would
    /// understate a live order.
    /// </para>
    /// </remarks>
    public static VenueOrderState ToVenueOrderState(ClientModels.OrderStatus status)
    {
        return status switch
        {
            ClientModels.OrderStatus.Working => VenueOrderState.Working,
            ClientModels.OrderStatus.PendingCancel => VenueOrderState.Working,
            ClientModels.OrderStatus.PendingReplace => VenueOrderState.Working,
            ClientModels.OrderStatus.PendingNew => VenueOrderState.Pending,
            ClientModels.OrderStatus.Filled => VenueOrderState.Filled,
            ClientModels.OrderStatus.Canceled => VenueOrderState.Cancelled,
            ClientModels.OrderStatus.Expired => VenueOrderState.Expired,
            ClientModels.OrderStatus.Rejected => VenueOrderState.Rejected,

            // Unknown, Completed, Suspended, or a status a newer client adds -- fail closed (gh#60).
            _ => VenueOrderState.Unknown,
        };
    }

    /// <summary>Maps Tradovate's buy/sell action onto the venue-neutral side.</summary>
    /// <param name="action">Tradovate's order action.</param>
    /// <returns>Buy or sell.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The action is not recognized.</exception>
    /// <remarks>
    /// Throws rather than defaulting: side is the sign of an execution, so guessing it would invert a position's
    /// direction in the journal — the one place a silent default is worse than a loud failure.
    /// </remarks>
    public static OrderSide ToVenueSide(ClientModels.OrderAction action)
    {
        return action switch
        {
            ClientModels.OrderAction.Buy => OrderSide.Buy,
            ClientModels.OrderAction.Sell => OrderSide.Sell,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unrecognized Tradovate order action."),
        };
    }

    /// <summary>Maps a Tradovate order entity onto the neutral <see cref="OrderStateEvent"/> (R-17, gh#977).</summary>
    /// <param name="order">The Tradovate order entity, from a <c>props</c> frame or a sync snapshot.</param>
    /// <param name="venue">The venue to tag the event with.</param>
    /// <returns>The neutral order-state event.</returns>
    /// <exception cref="TradovateVenueException">The order has no id.</exception>
    /// <remarks>
    /// <para>
    /// <b>Filled quantity and average fill price are reported absent, not zero.</b> Tradovate's order entity carries
    /// neither, and a 0 would be indistinguishable from "nothing has filled yet" — a fabricated figure on the path
    /// that feeds the journal. The neutral record models both as nullable for exactly this case; filled volume comes
    /// from <see cref="FillEvent"/>s, which are the authoritative record of executed size.
    /// </para>
    /// <para>
    /// <b><c>At</c> carries Tradovate's <i>create</i> timestamp, which is not quite what the field promises.</b> The
    /// neutral <see cref="AccountEvent.At"/> means "when the venue reported the change", and the ProjectX adapter can
    /// honour that because its update carries <c>UpdateTimestamp ?? CreationTimestamp</c>. A Tradovate <c>order</c>
    /// entity has only the one <c>timestamp</c> — the create time — so a state change reports the time the order was
    /// placed rather than the time it changed. Named rather than papered over: it is the real field, not an invented
    /// one, and its only consumer today is the realtime status push, where it shows an order's age rather than
    /// driving anything. A consumer that needs true change time must not read it from here.
    /// </para>
    /// </remarks>
    public static OrderStateEvent ToOrderStateEvent(ClientModels.Order order, VenueId venue)
    {
        ArgumentNullException.ThrowIfNull(order);

        // The id is the handle the journal resolves the order by; without it the event names nothing it could be
        // applied to, so refuse it here rather than emit an event keyed on an empty string.
        if (order.Id is not { } id)
        {
            throw new TradovateVenueException(
                $"Tradovate reported an order on account {order.AccountId} with no id, so it cannot be matched to a "
                + "journaled order.");
        }

        return new OrderStateEvent(
            VenueAccountId.Create(venue, order.AccountId.ToString(CultureInfo.InvariantCulture)),
            order.Timestamp,
            id.ToString(CultureInfo.InvariantCulture),
            ToVenueOrderState(order.OrdStatus),
            FilledQuantity: null,
            AverageFillPrice: null);
    }

    /// <summary>Maps a Tradovate fill entity onto the neutral <see cref="FillEvent"/> (R-17, gh#977).</summary>
    /// <param name="fill">The Tradovate fill entity.</param>
    /// <param name="account">
    /// The account the fill belongs to, <b>resolved by the caller</b>. Tradovate's fill entity carries only an order
    /// id — no account — so attribution is the streaming adapter's job (it holds the order → account map) and this
    /// mapping refuses to invent one. The handle is checked against <paramref name="venue"/> below.
    /// </param>
    /// <param name="venue">This adapter's venue — the account must belong to it.</param>
    /// <returns>The neutral fill event; the Tradovate fill id is its idempotency key.</returns>
    /// <exception cref="TradovateVenueException">The fill has no id.</exception>
    /// <exception cref="ArgumentException">The account belongs to another venue.</exception>
    /// <remarks>
    /// <para>
    /// <b>Fees are reported as zero, and that is a known gap.</b> Tradovate's fill entity carries no commission and
    /// the client exposes no per-fill fee anywhere, while the neutral <see cref="FillEvent"/> has no way to say
    /// "unknown". A zero UNDER-states cost, so realized P&amp;L reads slightly generous downstream — the direction
    /// that makes headroom look larger than it is on the R-5, R-9 and R-4 inputs. It is recorded rather than papered
    /// over with an invented number, and tracked as <b>gh#1068</b>; closing it needs a commission source the client
    /// does not yet surface, and possibly a nullable <c>Fees</c> so the absence is representable at all.
    /// </para>
    /// <para>
    /// <c>Active</c> is read as the bust flag: an inactive fill is a trade Tradovate has voided, and the consumer
    /// skips a voided fill rather than counting it.
    /// </para>
    /// </remarks>
    public static FillEvent ToFillEvent(ClientModels.Fill fill, VenueAccountId account, VenueId venue)
    {
        ArgumentNullException.ThrowIfNull(fill);

        // Checked, not assumed: Tradovate account handles are bare integers that collide freely with other venues',
        // so a foreign handle stamped onto a Tradovate execution would journal the fill against somebody else's
        // account. The attribution map this account came from is keyed by a bare Tradovate order id, which makes a
        // cross-venue mix-up a live possibility rather than a theoretical one.
        EnsureBelongsTo(account.Venue, venue, account.ToString());

        // The fill id is the { order, fill } idempotency key the persistence layer dedupes a replay on. Without it a
        // re-delivered fill would insert a second row and double-count a real execution.
        if (fill.Id is not { } id)
        {
            throw new TradovateVenueException(
                $"Tradovate reported a fill on order {fill.OrderId} with no id, so it carries no idempotency key.");
        }

        return new FillEvent(
            account,
            fill.Timestamp,
            fill.OrderId.ToString(CultureInfo.InvariantCulture),
            id.ToString(CultureInfo.InvariantCulture),
            ToVenueSide(fill.Action),
            fill.Qty,
            new Price(fill.Price),

            // Tradovate reports no per-fill commission -- see the remarks above. Not an omission; a named gap.
            Fees: 0m,

            // An inactive fill is a busted trade.
            Voided: !fill.Active);
    }

    /// <summary>Maps a Tradovate position entity onto the neutral <see cref="PositionEvent"/> (R-17, gh#977).</summary>
    /// <param name="position">The Tradovate position entity.</param>
    /// <param name="venue">The venue to tag the event with.</param>
    /// <returns>The neutral position event, with a signed net exposure.</returns>
    /// <exception cref="TradovateVenueException">An open position carries no net price.</exception>
    /// <remarks>
    /// Same discipline as <see cref="ToPositionSnapshot"/>, and the flat carve-out is the point: a <b>flat</b>
    /// position (<c>netPos</c> 0) is the OCO-cancel-on-exit trigger (gh#183) — the one position event that retires
    /// live protection — and its price is immaterial, so refusing it for a missing <c>netPrice</c> would leave a
    /// resting safety stop behind a position that no longer exists. An <b>open</b> position with no price is refused,
    /// because a fabricated 0 basis feeds a wildly wrong unrealised P&amp;L to the R-5 gate.
    /// </remarks>
    public static PositionEvent ToPositionEvent(ClientModels.Position position, VenueId venue)
    {
        ArgumentNullException.ThrowIfNull(position);

        if (position.NetPos != 0 && position.NetPrice is null)
        {
            throw new TradovateVenueException(
                $"Tradovate reported an open position ({position.NetPos} on contract {position.ContractId}) with no "
                + "net price, which cannot be mapped to an average entry price.");
        }

        return new PositionEvent(
            VenueAccountId.Create(venue, position.AccountId.ToString(CultureInfo.InvariantCulture)),
            position.Timestamp,
            VenueContractId.Create(venue, position.ContractId.ToString(CultureInfo.InvariantCulture)),
            position.NetPos,
            new Price(position.NetPrice ?? 0m));
    }

    private static void EnsureBelongsTo(VenueId actual, VenueId expected, string qualified)
    {
        if (actual != expected)
        {
            throw new ArgumentException(
                $"'{qualified}' belongs to venue '{actual}', not '{expected}'.", nameof(qualified));
        }
    }
}
