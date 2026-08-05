using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// Which price series the pivot search reads (gh#626). The choice moves every level in the system, so it is an
/// explicit option rather than a constant.
/// </summary>
public enum PivotSource
{
    /// <summary>Not a source — the refusable zero (the fail-closed-zero pattern, gh#60).</summary>
    Unknown = 0,

    /// <summary>
    /// The Heikin-Ashi candle body — <b>the default</b>. HA averages the bar's own four prices, so a level lands in
    /// the support/resistance <i>overlap</i> rather than on a single wick price never traded near twice.
    /// </summary>
    HeikinAshiBody = 1,

    /// <summary>The raw candle body (open/close), ignoring wicks entirely.</summary>
    Body = 2,

    /// <summary>The bar's own extremes — the widest reading, and the one a wick can dominate.</summary>
    HighLow = 3,
}

/// <summary>
/// Which side of price a key-level zone is (gh#626) — the domain twin of <c>Data.Entities.PriceLevelKind</c>.
/// </summary>
/// <remarks>
/// Deliberately duplicated rather than shared: <c>Domain</c> is the base layer and <c>Data</c> references it, never
/// the reverse, so a domain function cannot name the persistence enum. The values are kept numerically identical so
/// the host that persists (gh#597) maps by value, not by a switch that can drift.
/// </remarks>
public enum KeyLevelKind
{
    /// <summary>Not a side — the refusable zero.</summary>
    Unknown = 0,

    /// <summary>A floor: price has tended to hold above this zone.</summary>
    Support = 1,

    /// <summary>A ceiling: price has tended to hold below this zone.</summary>
    Resistance = 2,
}

/// <summary>One confirmed swing pivot — the turn a key-level zone is built around (gh#626).</summary>
/// <param name="BarIndex">Where in the supplied series the pivot sits.</param>
/// <param name="OpenTime">The pivot bar's own bucket — what the persisted zone records as its formation.</param>
/// <param name="Price">The pivot price, read from the configured <see cref="PivotSource"/>. <c>decimal</c>: money.</param>
/// <param name="Kind">A pivot high is a <see cref="KeyLevelKind.Resistance"/>; a pivot low a <see cref="KeyLevelKind.Support"/>.</param>
public sealed record SwingPivot(int BarIndex, DateTimeOffset OpenTime, decimal Price, KeyLevelKind Kind);

/// <summary>
/// The band a pivot projects — the value shape <c>Data.Entities.PriceLevel</c> persists (gh#596), produced but
/// never stored here.
/// </summary>
/// <param name="Bottom">The band's floor. Strictly below <paramref name="Top"/>, and positive.</param>
/// <param name="Top">The band's ceiling.</param>
/// <param name="Kind">Which side of price the band is.</param>
/// <param name="FormedAtBucket">The originating pivot bar's bucket.</param>
public sealed record KeyLevelZone(decimal Bottom, decimal Top, KeyLevelKind Kind, DateTimeOffset FormedAtBucket);

/// <summary>The knobs the pivot search and the zone width read (gh#626). Immutable; use <c>with</c> to vary one.</summary>
public sealed record KeyLevelOptions
{
    /// <summary>The shipped defaults — production's ~20/15 window on the Heikin-Ashi body.</summary>
    public static KeyLevelOptions Default { get; } = new();

    /// <summary>How many bars back a pivot must top. Positive.</summary>
    public int LeftBars { get; init; } = 20;

    /// <summary>How many bars forward must confirm it. Positive — the newest bars are never pivots yet.</summary>
    public int RightBars { get; init; } = 15;

    /// <summary>Which price series the search reads.</summary>
    public PivotSource Source { get; init; } = PivotSource.HeikinAshiBody;

    /// <summary>What fraction of ATR the zone's half-width is, before the cap and floor. Positive.</summary>
    public decimal AtrMultiple { get; init; } = 0.5m;

    /// <summary>The half-width ceiling as a fraction of price, so a volatile session cannot mint a level that swallows the chart. In (0, 1].</summary>
    public decimal MaxHalfWidthFraction { get; init; } = 0.05m;

    /// <summary>The half-width floor, so a dead-quiet session cannot collapse a zone to a line nothing can overlap. Positive.</summary>
    public decimal MinHalfWidth { get; init; } = 0.01m;
}

/// <summary>
/// The pure key-level function (gh#626, of gh#597; R-10 / R-22): swing pivots over a bar series, and the
/// ATR-normalised band each one projects.
/// </summary>
/// <remarks>
/// <b>A pure function of what is handed in</b> — no clock, no store, no DI — so the same bars always produce the
/// same zones and a rebuild restores history rather than rewriting it (ADR-0001). The host that schedules this and
/// persists <c>PriceLevel</c> rows is gh#597; ATR arrives as a <i>value</i> (gh#311 computes it), never as a
/// dependency read from here.
/// </remarks>
public static class KeyLevels
{
    /// <summary>Finds every confirmed swing pivot in <paramref name="bars"/>.</summary>
    /// <param name="bars">The series, in <b>ascending</b> time order.</param>
    /// <param name="options">The window and source to read.</param>
    /// <returns>The pivots, in ascending bar order. Empty when the series cannot hold a full window.</returns>
    /// <exception cref="ArgumentException"><paramref name="bars"/> is not in ascending time order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A window is not positive.</exception>
    public static IReadOnlyList<SwingPivot> FindPivots(IReadOnlyList<Bar> bars, KeyLevelOptions options)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.LeftBars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.RightBars);
        if (options.Source == PivotSource.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A pivot source is required (the zero is refusable).");
        }

        // Out of order does not fail on its own -- the window walks neighbours by INDEX, so a shuffled series
        // quietly reports pivots of a chart nobody is looking at. Refuse it here, as AverageTrueRange does.
        for (int i = 1; i < bars.Count; i++)
        {
            if (bars[i].OpenTime <= bars[i - 1].OpenTime)
            {
                throw new ArgumentException(
                    "Bars must be in ascending time order: a pivot is defined over its index neighbours, so a "
                    + "mis-ordered series computes a different, wrong set of levels rather than failing.",
                    nameof(bars));
            }
        }

        (decimal[] highs, decimal[] lows) = SourceSeries(bars, options.Source);

        List<SwingPivot> pivots = [];

        // A pivot needs a FULL window on both sides: the newest RightBars cannot be pivots yet however extreme
        // (nothing confirms them), and the oldest LeftBars cannot be shown to have topped anything.
        for (int i = options.LeftBars; i < bars.Count - options.RightBars; i++)
        {
            if (IsExtreme(highs, i, options, highest: true))
            {
                pivots.Add(new SwingPivot(i, bars[i].OpenTime, highs[i], KeyLevelKind.Resistance));
            }

            if (IsExtreme(lows, i, options, highest: false))
            {
                pivots.Add(new SwingPivot(i, bars[i].OpenTime, lows[i], KeyLevelKind.Support));
            }
        }

        return pivots;
    }

    /// <summary>
    /// Whether <paramref name="index"/> is the window's extreme, with the tie-break that makes a plateau ONE level.
    /// </summary>
    /// <remarks>
    /// <b>Strictly beyond on the left, at-or-beyond on the right.</b> Three equal highs in a row are one turn, not
    /// three levels — and without an asymmetry they are either three or none. Requiring strictness only on the left
    /// elects the <i>earliest</i> bar of a plateau: the bar where price first reached the level, which is the one
    /// the chart's own swing label sits on.
    /// </remarks>
    private static bool IsExtreme(decimal[] series, int index, KeyLevelOptions options, bool highest)
    {
        decimal candidate = series[index];

        for (int j = index - options.LeftBars; j < index; j++)
        {
            if (highest ? series[j] >= candidate : series[j] <= candidate)
            {
                return false;
            }
        }

        for (int j = index + 1; j <= index + options.RightBars; j++)
        {
            if (highest ? series[j] > candidate : series[j] < candidate)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The upper and lower price of each bar under the configured source.</summary>
    private static (decimal[] Highs, decimal[] Lows) SourceSeries(IReadOnlyList<Bar> bars, PivotSource source)
    {
        decimal[] highs = new decimal[bars.Count];
        decimal[] lows = new decimal[bars.Count];

        if (source != PivotSource.HeikinAshiBody)
        {
            for (int i = 0; i < bars.Count; i++)
            {
                Bar bar = bars[i];
                (highs[i], lows[i]) = source switch
                {
                    PivotSource.HighLow => (bar.High.Value, bar.Low.Value),
                    PivotSource.Body => (Math.Max(bar.Open.Value, bar.Close.Value), Math.Min(bar.Open.Value, bar.Close.Value)),
                    _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unhandled pivot source."),
                };
            }

            return (highs, lows);
        }

        // Heikin-Ashi. haClose averages the bar's own four prices; haOpen carries the previous synthetic candle
        // forward, which is what smooths the series -- and what makes it path-dependent, so it is computed in one
        // ascending pass rather than per bar.
        decimal haOpen = (bars.Count > 0 ? bars[0].Open.Value + bars[0].Close.Value : 0m) / 2m;
        for (int i = 0; i < bars.Count; i++)
        {
            Bar bar = bars[i];
            decimal haClose = (bar.Open.Value + bar.High.Value + bar.Low.Value + bar.Close.Value) / 4m;
            if (i > 0)
            {
                haOpen = (haOpen + PreviousHaClose(bars, i)) / 2m;
            }

            highs[i] = Math.Max(haOpen, haClose);
            lows[i] = Math.Min(haOpen, haClose);
        }

        return (highs, lows);
    }

    /// <summary>The previous bar's Heikin-Ashi close — the other half of the carry-forward.</summary>
    private static decimal PreviousHaClose(IReadOnlyList<Bar> bars, int index)
    {
        Bar previous = bars[index - 1];
        return (previous.Open.Value + previous.High.Value + previous.Low.Value + previous.Close.Value) / 4m;
    }

    /// <summary>Projects the band around a pivot: half-width <c>min(ATR × multiple, price × fraction)</c>, floored.</summary>
    /// <param name="pivot">The confirmed pivot.</param>
    /// <param name="atr">The ATR at the pivot bar, supplied by the caller. Not negative.</param>
    /// <param name="options">The width knobs.</param>
    /// <returns>The band, ordered and positive.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="atr"/> is negative, or a knob is out of range.</exception>
    public static KeyLevelZone ZoneFor(SwingPivot pivot, decimal atr, KeyLevelOptions options)
    {
        ArgumentNullException.ThrowIfNull(pivot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(atr);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pivot.Price);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.AtrMultiple);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MinHalfWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxHalfWidthFraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxHalfWidthFraction, 1m);

        // Volatility sets the width, but neither arm may run away: the ATR arm is capped at a fraction of price so
        // one violent session cannot mint a level that swallows the chart, and floored so a dead-quiet one cannot
        // collapse it to a line nothing can ever overlap or merge with.
        decimal halfWidth = Math.Max(
            options.MinHalfWidth,
            Math.Min(atr * options.AtrMultiple, pivot.Price * options.MaxHalfWidthFraction));

        // The floor and the positive-bottom invariant can disagree on a very low-priced instrument. Bottom-positive
        // is a fact about prices and a database check (gh#596); the floor is a tuning knob. The invariant wins.
        if (halfWidth >= pivot.Price)
        {
            halfWidth = pivot.Price / 2m;
        }

        return new KeyLevelZone(
            pivot.Price - halfWidth, pivot.Price + halfWidth, pivot.Kind, pivot.OpenTime);
    }
}
