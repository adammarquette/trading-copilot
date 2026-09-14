using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.Domain.Suggestions;

/// <summary>
/// Which evidence shape a <see cref="ConfluenceFactor"/> is (gh#730, ADR-0026) — the domain twin of
/// <c>Data.Entities.CitedFactorKind</c>, kept numerically identical so the issuance host maps by value, not by a
/// switch that can drift. <c>Domain</c> is the base layer and <c>Data</c> references it, never the reverse, so a
/// domain function cannot name the persistence enum.
/// </summary>
public enum ConfluenceFactorKind
{
    /// <summary>Not a kind — the refusable zero (the fail-closed-zero pattern, gh#60).</summary>
    Unknown = 0,

    /// <summary>An indicator read that fired — the <c>Indicator</c> / <c>Period</c> arm.</summary>
    Indicator = 1,

    /// <summary>A price level the entry sat near — the immutable level-snapshot arm.</summary>
    Level = 2,
}

/// <summary>
/// One <b>supporting</b> factor the confluence assembly produced (gh#730, ADR-0026, R-4) — a repeated signal on
/// another timeframe, or a level the entry sat near. Pure and entity-free: the issuance host maps this onto the
/// persisted <c>Data.Entities.CitedFactor</c>, copying it immutably at issuance (R-4). Never the primary — the
/// primary is the fired signal itself (gh#592's smallest timeframe), which the host adds separately.
/// </summary>
public sealed record ConfluenceFactor
{
    /// <summary>Which arm this factor fills.</summary>
    public required ConfluenceFactorKind Kind { get; init; }

    /// <summary>The timeframe this factor is read on, in minutes — the axis the min-rule ranks the indicator set by.</summary>
    public required int TimeframeMinutes { get; init; }

    // ---- Indicator arm (Kind = Indicator) ----

    /// <summary>The corroborating indicator — the SAME signal as the primary. Non-null iff <see cref="Kind"/> is Indicator.</summary>
    public string? Indicator { get; init; }

    /// <summary>The corroborating indicator's period — the primary's. Non-null iff <see cref="Kind"/> is Indicator.</summary>
    public int? Period { get; init; }

    // ---- Level-snapshot arm (Kind = Level): a COPY of the zone as it stood at issuance ----

    /// <summary>The source level's id — a soft reference the persisted snapshot carries. Non-null iff <see cref="Kind"/> is Level.</summary>
    public Guid? LevelId { get; init; }

    /// <summary>The venue the level was found on. Non-null iff <see cref="Kind"/> is Level.</summary>
    public string? LevelVenue { get; init; }

    /// <summary>Which side of price the zone is. Non-null iff <see cref="Kind"/> is Level.</summary>
    public KeyLevelKind? LevelKind { get; init; }

    /// <summary>The top of the zone band. Non-null iff <see cref="Kind"/> is Level.</summary>
    public decimal? LevelTop { get; init; }

    /// <summary>The bottom of the zone band. Non-null iff <see cref="Kind"/> is Level.</summary>
    public decimal? LevelBottom { get; init; }

    /// <summary>The detector significance (gh#597). Non-null iff <see cref="Kind"/> is Level.</summary>
    public decimal? LevelSignificance { get; init; }

    /// <summary>Builds an indicator-arm factor from a corroborating read on another timeframe.</summary>
    /// <param name="timeframeMinutes">The corroborating timeframe.</param>
    /// <param name="indicator">The primary's indicator name (the SAME signal).</param>
    /// <param name="period">The primary's period.</param>
    /// <returns>The supporting indicator factor.</returns>
    public static ConfluenceFactor ForIndicator(int timeframeMinutes, string indicator, int period) =>
        new()
        {
            Kind = ConfluenceFactorKind.Indicator,
            TimeframeMinutes = timeframeMinutes,
            Indicator = indicator,
            Period = period,
        };

    /// <summary>Builds a level-arm factor by copying a corroborating level's snapshot.</summary>
    /// <param name="level">The level the entry sat within the band of.</param>
    /// <returns>The supporting level factor, its zone copied immutably (R-4).</returns>
    public static ConfluenceFactor ForLevel(AlignableLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);

        return new ConfluenceFactor
        {
            Kind = ConfluenceFactorKind.Level,
            TimeframeMinutes = level.TimeframeMinutes,
            LevelId = level.LevelId,
            LevelVenue = level.LevelVenue,
            LevelKind = level.Kind,
            LevelTop = level.Top,
            LevelBottom = level.Bottom,
            LevelSignificance = level.Significance,
        };
    }
}

/// <summary>
/// One candidate indicator reading on a timeframe (gh#730) — the SAME signal as the fired primary, read on a
/// different bar size. A <see langword="null"/> <paramref name="Value"/> means <b>cannot measure</b>, and never
/// corroborates ("cannot measure ⇒ do not fabricate", the <c>KeyLevels</c> posture).
/// </summary>
/// <param name="TimeframeMinutes">The bar size the reading was computed over.</param>
/// <param name="Value">The indicator value, or <see langword="null"/> when none can be measured.</param>
public sealed record TimeframeReading(int TimeframeMinutes, decimal? Value);

/// <summary>
/// One active level, in the entity-free shape the alignment reads (gh#730, ADR-0026). The alignment measures
/// proximity from <see cref="Top"/> / <see cref="Bottom"/>; the remaining fields are the immutable snapshot copied
/// onto a corroborating <see cref="ConfluenceFactor"/> (R-4). <see cref="Kind"/> is the domain
/// <see cref="KeyLevelKind"/>, never <c>Data.PriceLevelKind</c>.
/// </summary>
/// <param name="TimeframeMinutes">The timeframe the level belongs to, in minutes.</param>
/// <param name="Kind">Which side of price the zone is.</param>
/// <param name="Top">The top of the zone band. Above <paramref name="Bottom"/>.</param>
/// <param name="Bottom">The bottom of the zone band.</param>
/// <param name="Significance">The detector's ranking score (gh#597). Higher is stronger.</param>
/// <param name="LevelId">The source level's id — the soft reference the snapshot carries.</param>
/// <param name="LevelVenue">The venue the level was found on.</param>
public sealed record AlignableLevel(
    int TimeframeMinutes,
    KeyLevelKind Kind,
    decimal Top,
    decimal Bottom,
    decimal Significance,
    Guid LevelId,
    string LevelVenue);

/// <summary>
/// The proximity band a level must sit within of the entry to corroborate (gh#730, ADR-0026 §3):
/// <c>min(k ticks, f × ATR)</c>, so it scales with the instrument's volatility rather than meaning something
/// different on every contract. When no ATR can be measured the tick arm stands alone.
/// </summary>
/// <param name="TickSize">The instrument's tick size — one arm of the band.</param>
/// <param name="Atr">The average true range, or <see langword="null"/> when none can be measured.</param>
/// <param name="KTicks">How many ticks the tick arm spans. Positive.</param>
/// <param name="FAtr">The ATR multiple the volatility arm spans. Positive.</param>
public sealed record ConfluenceBand(decimal TickSize, decimal? Atr, int KTicks, decimal FAtr)
{
    /// <summary>
    /// The band half-width: <c>min(KTicks × TickSize, FAtr × ATR)</c>, or the tick arm alone when
    /// <see cref="Atr"/> is <see langword="null"/>.
    /// </summary>
    public decimal Distance => Atr is { } atr
        ? Math.Min(KTicks * TickSize, FAtr * atr)
        : KTicks * TickSize;
}

/// <summary>
/// The pure confluence-alignment function (gh#730, of gh#593; ADR-0026 §3, R-4): given a fired <b>primary</b>
/// signal, corroborate it against the other signals and levels current at that instant, and return the
/// <b>supporting</b> factors — never the primary.
/// </summary>
/// <remarks>
/// <para>
/// <b>A pure function of what is handed in</b> — no clock, no store, no DI — the gh#626 <c>KeyLevels</c> shape, so
/// the same inputs always produce the same factors and the rule is unit-testable off the database. The scan host
/// (gh#730) reads the candidate signals and active levels and persists the resulting <c>CitedFactor</c> rows; none
/// of that happens here.
/// </para>
/// <para>
/// <b>Indicators.</b> A candidate joins when the <i>same</i> signal — <see cref="IndicatorThresholdCondition.Evaluate"/>
/// over the reading — is <see cref="ConditionSatisfaction.Satisfied"/> <b>and</b> the reading is on a timeframe other
/// than the primary's. A null / absent reading does not join (cannot measure ⇒ do not fabricate).
/// </para>
/// <para>
/// <b>Levels.</b> A level joins when the distance from the entry to its zone <c>[Bottom, Top]</c> is within the
/// <see cref="ConfluenceBand"/> — zero when the entry is inside the zone, else the gap to the nearest edge. A level
/// never fires, so it is never the primary (ADR-0026).
/// </para>
/// </remarks>
public static class ConfluenceAlignment
{
    /// <summary>
    /// Aligns the fired <paramref name="primarySignal"/> against the candidate signals and active levels, returning
    /// the supporting factors only.
    /// </summary>
    /// <param name="primarySignal">The fired signal — its pure <see cref="IndicatorThresholdCondition.Evaluate"/> tests each candidate reading.</param>
    /// <param name="primaryTimeframeMinutes">The fired signal's timeframe — never re-emitted as a supporting factor.</param>
    /// <param name="candidateSignals">The same signal read on other timeframes.</param>
    /// <param name="activeLevels">The active levels to test the entry's proximity against.</param>
    /// <param name="entryPrice">The proposed entry price.</param>
    /// <param name="band">The proximity band a level must sit within of the entry.</param>
    /// <returns>The supporting factors — corroborating indicators and in-band levels. Never the primary.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    public static IReadOnlyList<ConfluenceFactor> AlignSupporting(
        IndicatorThresholdCondition primarySignal,
        int primaryTimeframeMinutes,
        IReadOnlyList<TimeframeReading> candidateSignals,
        IReadOnlyList<AlignableLevel> activeLevels,
        decimal entryPrice,
        ConfluenceBand band)
    {
        ArgumentNullException.ThrowIfNull(primarySignal);
        ArgumentNullException.ThrowIfNull(candidateSignals);
        ArgumentNullException.ThrowIfNull(activeLevels);
        ArgumentNullException.ThrowIfNull(band);

        List<ConfluenceFactor> supporting = [];

        // Indicator corroboration: the SAME signal, satisfied, on a DIFFERENT timeframe. The primary's own timeframe
        // is the headline (gh#592), never re-emitted; an Unmeasurable / NotSatisfied / Indeterminate read never joins.
        foreach (TimeframeReading reading in candidateSignals)
        {
            if (reading.TimeframeMinutes != primaryTimeframeMinutes
                && primarySignal.Evaluate(reading.Value) == ConditionSatisfaction.Satisfied)
            {
                supporting.Add(ConfluenceFactor.ForIndicator(
                    reading.TimeframeMinutes, primarySignal.Indicator, primarySignal.Period));
            }
        }

        // Level corroboration: the entry within the band of the zone. A level does not fire, so it is only ever
        // supporting -- the host appends it as IsPrimary=false.
        decimal distance = band.Distance;
        foreach (AlignableLevel level in activeLevels)
        {
            if (DistanceToZone(entryPrice, level.Bottom, level.Top) <= distance)
            {
                supporting.Add(ConfluenceFactor.ForLevel(level));
            }
        }

        return supporting;
    }

    /// <summary>The distance from <paramref name="price"/> to the zone <c>[bottom, top]</c> — zero when inside it.</summary>
    private static decimal DistanceToZone(decimal price, decimal bottom, decimal top)
    {
        if (price < bottom)
        {
            return bottom - price;
        }

        if (price > top)
        {
            return price - top;
        }

        return 0m; // inside the zone -- as close as it gets
    }
}
