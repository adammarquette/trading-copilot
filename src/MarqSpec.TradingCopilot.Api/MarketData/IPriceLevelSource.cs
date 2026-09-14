using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The read surface over persisted key-level zones (gh#596, R-10) — the shape confluence (gh#593) and any chart
/// overlay consult: the <b>active</b> levels for an instrument across a set of timeframes.
/// </summary>
public interface IPriceLevelSource
{
    /// <summary>
    /// Returns the active <see cref="PriceLevel"/> zones for one instrument on the given timeframes, strongest
    /// first within each timeframe. Retired / evicted levels are excluded.
    /// </summary>
    /// <param name="venue">The venue the levels were found on.</param>
    /// <param name="instrument">The venue-neutral instrument symbol.</param>
    /// <param name="timeframeMinutes">The timeframes to include; an empty set returns nothing.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The active levels, ordered by timeframe then descending significance.</returns>
    Task<IReadOnlyList<PriceLevel>> GetActiveLevelsAsync(
        string venue,
        string instrument,
        IReadOnlyCollection<int> timeframeMinutes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the active <see cref="PriceLevel"/> zones for one instrument on the given timeframes <b>across every
    /// venue</b>, strongest first within each timeframe. The <b>venue-agnostic</b> overload confluence (gh#730) reads:
    /// the scan holds only the venue-neutral instrument symbol (a trigger stores no venue), so it corroborates against
    /// levels regardless of which venue detected them — each cited level snapshots its own <see cref="PriceLevel.Venue"/>.
    /// Mirrors <c>StoredIndicatorSource</c>'s venue-neutral read; retired / evicted levels are excluded.
    /// </summary>
    /// <param name="instrument">The venue-neutral instrument symbol.</param>
    /// <param name="timeframeMinutes">The timeframes to include; an empty set returns nothing.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The active levels, ordered by timeframe then descending significance.</returns>
    Task<IReadOnlyList<PriceLevel>> GetActiveLevelsAsync(
        string instrument,
        IReadOnlyCollection<int> timeframeMinutes,
        CancellationToken cancellationToken);
}
