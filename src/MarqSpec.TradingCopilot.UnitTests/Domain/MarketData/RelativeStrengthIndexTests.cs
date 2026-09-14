using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.MarketData;

/// <summary>
/// Relative strength index (R-22) — the second indicator, and the proof the pipeline is a framework, not an ATR
/// special case.
/// </summary>
/// <remarks>
/// <para>
/// RSI reads the <b>close</b> only, and shares ATR's <b>Wilder smoothing</b>. The worked series below is chosen so
/// the seed lands on exactly 75: over closes 100 → 110 → 105 → 110 the moves are +10, −5, +5, so average gain 5,
/// average loss 5/3, relative strength 3, and <c>100 − 100/(1+3) = 75</c>. An indicator nobody can check by hand
/// is one nobody can debug.
/// </para>
/// <para>
/// The degenerate ratios are pinned deliberately: only-gains is 100, only-losses is 0, and a flat window is the
/// neutral 50 rather than a spurious extreme or a divide-by-zero.
/// </para>
/// </remarks>
public class RelativeStrengthIndexTests
{
    private static DateTimeOffset Open(int minute) => new(2026, 7, 20, 14, minute, 0, TimeSpan.Zero);

    private static Bar At(int minute, decimal close) =>
        new(Open(minute), new Price(close), new Price(close), new Price(close), new Price(close), 100);

    /// <summary>Closes 100, 110, 105, 110, 100 — moves +10, −5, +5, −10; first RSI (period 3) is exactly 75.</summary>
    private static IReadOnlyList<Bar> WorkedSeries() =>
    [
        At(0, 100), // no previous close, so no move
        At(1, 110), // +10
        At(2, 105), // −5
        At(3, 110), // +5   -> avgGain 5, avgLoss 5/3, RS 3, RSI = 100 − 100/4 = 75
        At(4, 100), // −10  -> Wilder smooths avgGain to 10/3, avgLoss to 40/9, RS 0.75, RSI ≈ 42.857
    ];

    // --- The arithmetic, checkable by eye ---

    [Fact]
    public void Compute_ShouldSeedFromTheAverageGainAndLoss()
    {
        IReadOnlyList<decimal?> rsi = RelativeStrengthIndex.Compute(WorkedSeries(), period: 3);

        rsi[3].Should().Be(75m);
    }

    [Fact]
    public void Compute_ShouldSmoothWilderStyle_AfterTheSeed()
    {
        // (avgGain*(p-1)+gain)/p and likewise for loss: avgGain 10/3, avgLoss 40/9, RS 0.75, RSI = 100 − 100/1.75.
        IReadOnlyList<decimal?> rsi = RelativeStrengthIndex.Compute(WorkedSeries(), period: 3);

        rsi[4]!.Value.Should().BeApproximately(42.857142m, 0.0001m);
    }

    // --- The degenerate ratios are pinned, not left to divide by zero ---

    [Fact]
    public void Compute_ShouldBe100_WhenEveryMoveIsAGain()
    {
        IReadOnlyList<Bar> rising = [At(0, 100), At(1, 101), At(2, 102), At(3, 103)];

        RelativeStrengthIndex.Compute(rising, period: 3)[3].Should().Be(100m);
    }

    [Fact]
    public void Compute_ShouldBe0_WhenEveryMoveIsALoss()
    {
        IReadOnlyList<Bar> falling = [At(0, 103), At(1, 102), At(2, 101), At(3, 100)];

        RelativeStrengthIndex.Compute(falling, period: 3)[3].Should().Be(0m);
    }

    [Fact]
    public void Compute_ShouldBeNeutral50_WhenTheWindowIsFlat()
    {
        // Neither gain nor loss: RSI is undefined by the ratio, but 50 (no pressure either way) is the honest
        // answer -- not the 100 that a naive "no losses" short-circuit would hand back.
        IReadOnlyList<Bar> flat = [At(0, 100), At(1, 100), At(2, 100), At(3, 100)];

        RelativeStrengthIndex.Compute(flat, period: 3)[3].Should().Be(50m);
    }

    [Fact]
    public void Compute_ShouldStayWithinZeroToOneHundred()
    {
        IReadOnlyList<decimal?> rsi = RelativeStrengthIndex.Compute(WorkedSeries(), period: 3);

        rsi.Where(value => value is not null)
            .Should().AllSatisfy(value => value!.Value.Should().BeInRange(0m, 100m));
    }

    // --- No value is better than a wrong one ---

    [Fact]
    public void Compute_ShouldYieldNoValue_UntilThePeriodIsSatisfied()
    {
        IReadOnlyList<decimal?> rsi = RelativeStrengthIndex.Compute(WorkedSeries(), period: 3);

        rsi.Take(3).Should().AllSatisfy(value => value.Should().BeNull());
    }

    [Fact]
    public void Compute_ShouldYieldNothing_WhenThereAreFewerBarsThanThePeriodNeeds()
    {
        // period + 1 closes are needed: the first bar has no previous close, so it has no move.
        IReadOnlyList<decimal?> rsi = RelativeStrengthIndex.Compute([.. WorkedSeries().Take(3)], period: 3);

        rsi.Should().AllSatisfy(value => value.Should().BeNull());
    }

    [Fact]
    public void Compute_ShouldYieldNothing_WhenThereAreNoBars()
    {
        RelativeStrengthIndex.Compute([], period: 3).Should().BeEmpty();
    }

    // --- Rebuild = replay ---

    [Fact]
    public void Compute_ShouldBeDeterministic_WhenRecomputedOverTheSameBars()
    {
        IReadOnlyList<decimal?> first = RelativeStrengthIndex.Compute(WorkedSeries(), period: 3);
        IReadOnlyList<decimal?> second = RelativeStrengthIndex.Compute(WorkedSeries(), period: 3);

        second.Should().Equal(first);
    }

    [Fact]
    public void Compute_ShouldBeAlignedToTheInputBars()
    {
        IReadOnlyList<Bar> bars = WorkedSeries();

        RelativeStrengthIndex.Compute(bars, period: 3).Should().HaveCount(bars.Count);
    }

    // --- Refuse nonsense rather than compute it ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Compute_ShouldRejectANonPositivePeriod(int period)
    {
        Action act = () => RelativeStrengthIndex.Compute(WorkedSeries(), period);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Compute_ShouldRejectBarsOutOfTimeOrder()
    {
        // RSI reads the PREVIOUS close for every move, so a mis-ordered series computes a wrong number silently.
        List<Bar> shuffled = [.. WorkedSeries()];
        (shuffled[1], shuffled[2]) = (shuffled[2], shuffled[1]);

        Action act = () => RelativeStrengthIndex.Compute(shuffled, period: 3);

        act.Should().Throw<ArgumentException>();
    }
}
