using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Reads indicator values from the projection store (gh#310) — the <see cref="IIndicatorSource"/> the execution
/// path consults.
/// </summary>
/// <remarks>
/// Reads the value at or <b>before</b> the requested moment, never after it. That is what keeps a replay or a
/// journal read honest: asking what the ATR was when a decision was taken must not return a number computed
/// afterwards.
/// </remarks>
public sealed class StoredIndicatorSource : IIndicatorSource
{
    private readonly TradingCopilotDbContext _database;
    private readonly IndicatorOptions _options;

    /// <summary>Creates the source over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="options">Supplies the period, which is part of a value's identity.</param>
    public StoredIndicatorSource(TradingCopilotDbContext database, IOptions<IndicatorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<decimal?> GetValueAsync(
        InstrumentId instrument,
        string indicator,
        int period,
        int resolutionMinutes,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        string symbol = instrument.ToString();

        return await _database.IndicatorValues
            .Where(value => value.Instrument == symbol
                && value.ResolutionMinutes == resolutionMinutes
                && value.Indicator == indicator
                && value.Period == period
                && value.BucketStart <= asOf)
            .OrderByDescending(value => value.BucketStart)
            .Select(value => (decimal?)value.Value)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<decimal?> GetAverageTrueRangeAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        DateTimeOffset asOf,
        CancellationToken cancellationToken) =>
        GetValueAsync(instrument, AtrIndicator.IndicatorName, _options.AtrPeriod, resolutionMinutes, asOf, cancellationToken);
}
