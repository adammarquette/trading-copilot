using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Suggestions;

/// <summary>
/// The pure confluence-alignment rule (gh#730, ADR-0026 §3, R-4): given a fired primary signal, corroborate it
/// against the signals + levels current at that instant and return the <b>supporting</b> factors only. An indicator
/// corroborates when the <b>same</b> signal is satisfied on <b>another</b> timeframe; a level corroborates when the
/// entry sits within a proximity band <c>min(k ticks, f×ATR)</c> of the zone. A level is never the primary (it does
/// not fire), and the primary's own timeframe is the headline, never re-emitted as supporting. Pure and entity-free
/// — proven here on synthetic inputs, the gh#626 <c>KeyLevels</c> pure-function shape.
/// </summary>
public class ConfluenceAlignmentTests
{
    private const int PrimaryTimeframe = 5;

    // The fired primary: RSI(14) on 5m is BELOW 30 — the smallest-timeframe headline (gh#592).
    private static readonly IndicatorThresholdCondition _primary = new(
        InstrumentId.Parse("ES"), "rsi", 14, PrimaryTimeframe, IndicatorComparison.Below, 30m);

    private static ConfluenceBand Band(decimal tick = 0.25m, decimal? atr = null, int kTicks = 8, decimal fAtr = 0.5m) =>
        new(tick, atr, kTicks, fAtr);

    private static AlignableLevel Level(
        int timeframe, decimal bottom, decimal top, KeyLevelKind kind = KeyLevelKind.Support,
        decimal significance = 5m, string venue = "TOPSTEPX") =>
        new(timeframe, kind, top, bottom, significance, Guid.NewGuid(), venue);

    private static IReadOnlyList<ConfluenceFactor> Align(
        IReadOnlyList<TimeframeReading>? readings = null,
        IReadOnlyList<AlignableLevel>? levels = null,
        decimal entry = 100m,
        ConfluenceBand? band = null) =>
        ConfluenceAlignment.AlignSupporting(
            _primary, PrimaryTimeframe, readings ?? [], levels ?? [], entry, band ?? Band());

    // --- Indicator corroboration: the SAME signal, on ANOTHER timeframe ---

    [Fact]
    public void AlignSupporting_ShouldCorroborate_WhenTheSameSignalIsSatisfiedOnALargerTimeframe()
    {
        // 25 is below 30 -> the same RSI(14)-below-30 signal is satisfied on the 60m too. It supports the 5m headline.
        ConfluenceFactor factor = Align(readings: [new TimeframeReading(60, 25m)]).Should().ContainSingle().Subject;

        factor.Kind.Should().Be(ConfluenceFactorKind.Indicator);
        factor.TimeframeMinutes.Should().Be(60);
        factor.Indicator.Should().Be("rsi", "the supporting indicator is the SAME signal as the primary");
        factor.Period.Should().Be(14);
    }

    [Fact]
    public void AlignSupporting_ShouldExclude_AReadingThatDoesNotSatisfyTheSignal()
    {
        // 45 is not below 30 -> the signal is NOT satisfied on the 60m, so it does not corroborate.
        Align(readings: [new TimeframeReading(60, 45m)]).Should().BeEmpty();
    }

    [Fact]
    public void AlignSupporting_ShouldNeverReEmitThePrimarysOwnTimeframe_EvenWhenSatisfied()
    {
        // A satisfied reading on the primary's OWN timeframe is the headline itself, never a supporting corroborator.
        Align(readings: [new TimeframeReading(PrimaryTimeframe, 25m)]).Should().BeEmpty();
    }

    [Fact]
    public void AlignSupporting_ShouldNotFabricate_FromANullReading()
    {
        // "cannot measure => do not fabricate" (the KeyLevels posture): a null / absent reading never corroborates.
        Align(readings: [new TimeframeReading(60, null)]).Should().BeEmpty();
    }

    // --- Level corroboration: the entry within min(k ticks, f×ATR) of the zone ---

    [Fact]
    public void AlignSupporting_ShouldCorroborate_WhenTheEntrySitsWithinTheBandOfALevel()
    {
        // Entry 100; zone [98, 99]; tick-arm band = 8 * 0.25 = 2.0; gap to the near edge (99) = 1.0 <= 2.0 -> joins,
        // and the level snapshot is COPIED onto the factor (R-4).
        AlignableLevel level = Level(60, 98m, 99m, KeyLevelKind.Resistance, significance: 7m);

        ConfluenceFactor factor = Align(levels: [level], entry: 100m).Should().ContainSingle().Subject;

        factor.Kind.Should().Be(ConfluenceFactorKind.Level);
        factor.TimeframeMinutes.Should().Be(60);
        factor.LevelTop.Should().Be(99m);
        factor.LevelBottom.Should().Be(98m);
        factor.LevelKind.Should().Be(KeyLevelKind.Resistance);
        factor.LevelSignificance.Should().Be(7m);
        factor.LevelId.Should().Be(level.LevelId, "the snapshot carries the soft level id");
        factor.LevelVenue.Should().Be(level.LevelVenue);
    }

    [Fact]
    public void AlignSupporting_ShouldExclude_ALevelBeyondTheBand()
    {
        // Entry 100; zone [90, 95]; gap to the near edge (95) = 5.0 > 2.0 tick-arm -> excluded.
        Align(levels: [Level(60, 90m, 95m)], entry: 100m).Should().BeEmpty();
    }

    [Fact]
    public void AlignSupporting_ShouldCorroborate_WhenTheEntryIsInsideTheZone()
    {
        // Inside the zone is distance 0 -> it corroborates regardless of how narrow the band is (here the ATR arm is 0).
        Align(levels: [Level(60, 98m, 102m)], entry: 100m, band: Band(atr: 0m)).Should().ContainSingle();
    }

    [Fact]
    public void AlignSupporting_ShouldUseTheAtrArm_WhenItIsTheSmaller()
    {
        // k*tick = 8 * 0.25 = 2.0; f*ATR = 0.5 * 1.0 = 0.5 -> min is the ATR arm (0.5). A level 1.0 away is EXCLUDED.
        ConfluenceBand band = Band(tick: 0.25m, atr: 1.0m, kTicks: 8, fAtr: 0.5m);

        Align(levels: [Level(60, 98m, 99m)], entry: 100m, band: band)
            .Should().BeEmpty("the ATR arm (0.5) binds and the 1.0 gap exceeds it");
    }

    [Fact]
    public void AlignSupporting_ShouldFallBackToTheTickArm_WhenAtrIsNull()
    {
        // A null ATR leaves the tick arm alone (2.0); the 1.0-away level now joins.
        ConfluenceBand band = Band(tick: 0.25m, atr: null, kTicks: 8, fAtr: 0.5m);

        Align(levels: [Level(60, 98m, 99m)], entry: 100m, band: band).Should().ContainSingle();
    }

    // --- Both arms together, and the degenerate empty case ---

    [Fact]
    public void AlignSupporting_ShouldReturnBothArms_WhenAnIndicatorAndALevelBothCorroborate()
    {
        IReadOnlyList<ConfluenceFactor> supporting = Align(
            readings: [new TimeframeReading(60, 25m)],
            levels: [Level(15, 98m, 99m)],
            entry: 100m);

        supporting.Should().HaveCount(2);
        supporting.Count(f => f.Kind == ConfluenceFactorKind.Indicator).Should().Be(1);
        supporting.Count(f => f.Kind == ConfluenceFactorKind.Level).Should().Be(1);
    }

    [Fact]
    public void AlignSupporting_ShouldReturnEmpty_WhenNothingCorroborates()
    {
        // No candidate readings and no levels -> no supporting factors: the N=1 "set of one" every suggestion is today.
        ConfluenceAlignment.AlignSupporting(_primary, PrimaryTimeframe, [], [], 100m, Band()).Should().BeEmpty();
    }

    [Fact]
    public void AlignSupporting_ShouldThrow_OnANullPrimarySignal()
    {
        Action act = () => ConfluenceAlignment.AlignSupporting(null!, PrimaryTimeframe, [], [], 100m, Band());

        act.Should().Throw<ArgumentNullException>();
    }
}
