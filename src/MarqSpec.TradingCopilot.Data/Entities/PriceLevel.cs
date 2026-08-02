namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A persisted <b>key-level zone</b> (data dictionary §2, R-10 / R-22, gh#596) — a support or resistance band the
/// price has respected on a given timeframe. The <b>store</b> half of key-level detection: this defines and holds
/// the shape; the swing-pivot / ATR detector that populates it is <see href="https://github.com/adammarquette/trading-copilot/issues/597">gh#597</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <c>IUserOwned</c>.</b> Like <see cref="BarRecord"/> and <see cref="IndicatorValueRecord"/>,
/// derived market data is <b>shared / global</b> (R-20): a tenant filter here would hide the market from the
/// operator trading it. It belongs to its <see cref="TimeframeMinutes"/> — a level found on the 60-minute chart is
/// a different object from one on the 5-minute, and confluence across timeframes (gh#593) is the point.
/// </para>
/// <para>
/// Unlike the append-only time-series records, a level is a <b>mutable</b> object: aligned pivots merge into it
/// (<see cref="TouchCount"/>), it is re-scored, and it is retired (<see cref="Active"/>) — so it carries a synthetic
/// <see cref="Id"/> rather than a natural time-series key.
/// </para>
/// </remarks>
public sealed class PriceLevel
{
    /// <summary>The zone's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The venue the level was found on — the same product on two venues is two series.</summary>
    public required string Venue { get; set; }

    /// <summary>The venue-neutral instrument symbol (e.g. <c>ES</c>).</summary>
    public required string Instrument { get; set; }

    /// <summary>The timeframe the level belongs to, in minutes — the axis confluence (gh#593) aligns across.</summary>
    public required int TimeframeMinutes { get; set; }

    /// <summary>The top of the zone band. <b>decimal</b>: price is money, never <c>float</c>. Strictly above <see cref="Bottom"/>.</summary>
    public required decimal Top { get; set; }

    /// <summary>The bottom of the zone band. Strictly below <see cref="Top"/>, and positive.</summary>
    public required decimal Bottom { get; set; }

    /// <summary>Whether the zone is a floor or a ceiling. <see cref="PriceLevelKind.Unknown"/> is refused by a DB check.</summary>
    public required PriceLevelKind Kind { get; set; }

    /// <summary>How extreme the originating pivot is — the detector's ranking score (gh#597). Higher is stronger.</summary>
    public required decimal Significance { get; set; }

    /// <summary>The bucket start of the pivot bar the zone formed on. Always UTC.</summary>
    public required DateTimeOffset FormedAtBucket { get; set; }

    /// <summary>How many aligned pivots merged into this zone — level-confluence within a timeframe. At least one.</summary>
    public required int TouchCount { get; set; }

    /// <summary>Whether the zone is live. A retired / evicted level is kept for the journal but read out by default.</summary>
    public required bool Active { get; set; }

    /// <summary>When this row was last written — a merge or a re-score updates it.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}
