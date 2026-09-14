namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One pre-computed indicator value — a <b>projection</b> over the clean-historical bar store (gh#310, R-1,
/// ADR-0001).
/// </summary>
/// <remarks>
/// <para>
/// ADR-0001: *"Indicators are projections over the log… Rebuild = replay"*, with the later correction that a full
/// rebuild reprocesses the clean-historical store (gh#302) rather than the 24-hour event log. So nothing here is
/// authoritative: every row is reproducible from <see cref="BarRecord"/>, and that is the point.
/// </para>
/// <para>
/// The key carries <b>indicator and period</b> as well as the series, because an ATR(14) and an ATR(3) are
/// different numbers over the same bars. A consumer asking for one must never be handed the other — this value
/// sets where a live position's stop sits (gh#311).
/// </para>
/// <para>
/// <b>Global, not <c>IUserOwned</c></b> — derived market data, and R-20 puts market data on the shared side of
/// its own line.
/// </para>
/// </remarks>
public sealed class IndicatorValueRecord
{
    /// <summary>The venue the underlying bars came from.</summary>
    public required string Venue { get; set; }

    /// <summary>The venue-neutral instrument symbol (e.g. <c>ES</c>).</summary>
    public required string Instrument { get; set; }

    /// <summary>The bar size the indicator was computed over, in minutes.</summary>
    public required int ResolutionMinutes { get; set; }

    /// <summary>The indicator's name (e.g. <c>atr</c>). Part of the key so a second indicator needs no reshape.</summary>
    public required string Indicator { get; set; }

    /// <summary>The indicator's period parameter — part of the identity, not a detail.</summary>
    public required int Period { get; set; }

    /// <summary>The bar bucket this value belongs to — the hypertable's time dimension. Always UTC.</summary>
    public required DateTimeOffset BucketStart { get; set; }

    /// <summary>The computed value.</summary>
    public required decimal Value { get; set; }

    /// <summary>When this row was last computed. A revised bar updates the value and this stamp together.</summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
