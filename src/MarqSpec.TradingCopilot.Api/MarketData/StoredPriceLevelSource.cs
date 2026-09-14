using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Reads key-level zones from the store (gh#596) — the <see cref="IPriceLevelSource"/> confluence and overlays
/// consult. Reads only <b>active</b> levels; a retired zone stays in the table for the journal but is not read out.
/// </summary>
public sealed class StoredPriceLevelSource : IPriceLevelSource
{
    private readonly TradingCopilotDbContext _database;

    /// <summary>Creates the source over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    public StoredPriceLevelSource(TradingCopilotDbContext database)
    {
        _database = database;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PriceLevel>> GetActiveLevelsAsync(
        string venue,
        string instrument,
        IReadOnlyCollection<int> timeframeMinutes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        ArgumentNullException.ThrowIfNull(timeframeMinutes);

        // An empty set asks for no timeframes -- return nothing rather than everything, so a caller must name the
        // timeframes it wants (a level belongs to exactly one, and confluence reads a specific set).
        if (timeframeMinutes.Count == 0)
        {
            return [];
        }

        return await _database.PriceLevels
            .AsNoTracking()
            .Where(level => level.Venue == venue
                && level.Instrument == instrument
                && level.Active
                && timeframeMinutes.Contains(level.TimeframeMinutes))
            .OrderBy(level => level.TimeframeMinutes)
            .ThenByDescending(level => level.Significance)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PriceLevel>> GetActiveLevelsAsync(
        string instrument,
        IReadOnlyCollection<int> timeframeMinutes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        ArgumentNullException.ThrowIfNull(timeframeMinutes);

        // An empty set asks for no timeframes -- return nothing rather than everything, so a caller must name the
        // timeframes it wants (the venue-taking overload's rule).
        if (timeframeMinutes.Count == 0)
        {
            return [];
        }

        // No Venue filter (the venue-agnostic overload, gh#730): the scan holds only the venue-neutral symbol, so this
        // reads every venue's active levels for it -- exactly as StoredIndicatorSource reads indicator values without a
        // venue. PriceLevel is shared / global market data (not IUserOwned), so there is no owner filter to cross.
        return await _database.PriceLevels
            .AsNoTracking()
            .Where(level => level.Instrument == instrument
                && level.Active
                && timeframeMinutes.Contains(level.TimeframeMinutes))
            .OrderBy(level => level.TimeframeMinutes)
            .ThenByDescending(level => level.Significance)
            .ToListAsync(cancellationToken);
    }
}
