namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One stored OHLCV bar — the <b>clean-historical system of record</b> (gh#302, R-1, ADR-0001).
/// </summary>
/// <remarks>
/// <para>
/// R-1 keeps the two market-data paths apart on purpose: the live stream is <i>not</i> the authoritative series.
/// This table is, and it is what ADR-0001 means by *"a full indicator rebuild reprocesses the clean historical
/// store, not the short event log"*. The event log holds <c>market.quote</c> for 24 hours and is a pipeline;
/// this holds bars for as long as the operator keeps them, and is a record.
/// </para>
/// <para>
/// <b>Deliberately not <c>IUserOwned</c>.</b> R-20 draws the line explicitly — operator-owned rows carry an
/// owning identity, while *"reference &amp; market data (instruments, venues, data providers, bars/ticks/quotes,
/// raw news) is shared / global"*. A tenant filter here would be a defect, not a safeguard: it would hide the
/// market from the operator who is trading it.
/// </para>
/// <para>
/// The key is (venue, instrument, resolution, bucket). Resolution is part of it because a 1-minute and a
/// 5-minute bar can open at the same instant, and keyed on time alone they would silently overwrite each other.
/// </para>
/// </remarks>
public sealed class BarRecord
{
    /// <summary>The venue the bars came from — the same product on two venues is two series.</summary>
    public required string Venue { get; set; }

    /// <summary>The venue-neutral instrument symbol (e.g. <c>ES</c>).</summary>
    public required string Instrument { get; set; }

    /// <summary>The bar size in minutes. Part of the key: resolutions are independent series.</summary>
    public required int ResolutionMinutes { get; set; }

    /// <summary>
    /// When the bar opened — the hypertable's time dimension, and the bucket identity. Always UTC (gh#201).
    /// </summary>
    public required DateTimeOffset BucketStart { get; set; }

    /// <summary>The opening price.</summary>
    public required decimal Open { get; set; }

    /// <summary>The highest traded price in the bar.</summary>
    public required decimal High { get; set; }

    /// <summary>The lowest traded price in the bar.</summary>
    public required decimal Low { get; set; }

    /// <summary>The closing price.</summary>
    public required decimal Close { get; set; }

    /// <summary>The traded volume in the bar.</summary>
    public required long Volume { get; set; }

    /// <summary>
    /// When this row was last written. A revision updates it, so "when did we learn this" stays answerable
    /// separately from "when did the market do this".
    /// </summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
