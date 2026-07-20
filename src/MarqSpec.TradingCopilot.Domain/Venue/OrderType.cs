namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// The venue-neutral order types. Deliberately the common set — venue-specific extras (ProjectX
/// <c>JoinBid</c>/<c>JoinAsk</c>) stay out of the core, and types not every venue offers are gated by
/// <see cref="VenueCapability"/> rather than assumed (R-17).
/// </summary>
public enum OrderType
{
    /// <summary>Market order.</summary>
    Market,

    /// <summary>Limit order — requires a limit price.</summary>
    Limit,

    /// <summary>Stop order — requires a stop price.</summary>
    Stop,

    /// <summary>Stop-limit order — requires both a stop and a limit price.</summary>
    StopLimit,

    /// <summary>Trailing stop — gated by <see cref="VenueCapability.TrailingStops"/>.</summary>
    TrailingStop,
}
