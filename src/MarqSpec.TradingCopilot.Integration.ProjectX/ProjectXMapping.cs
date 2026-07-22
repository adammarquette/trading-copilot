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
    /// <see cref="TradingMode"/> is resolved through <paramref name="conventions"/> — <b>not</b> from the
    /// gateway's <c>simulated</c> flag. That flag says where an order executes, which on a prop platform is
    /// close to orthogonal to what is at stake: a funded account reports <c>simulated=true</c> and executes on
    /// a simulated engine, yet a breach costs a real payout. Reading it as economic stake classified exactly
    /// the account that matters most as harmless (gh#60).
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
            conventions.ModeFor(stage))
        {
            Stage = stage,
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
