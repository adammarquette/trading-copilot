using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Domain;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The per-instrument contract-spec source (gh#541, R-4 / R-16): the tick size, point value and catastrophic
/// safety-stop distance a <b>server-originated</b> proposal needs before it can become an order ticket.
/// </summary>
/// <remarks>
/// The properties that matter: known CME products resolve out of the box (mirroring the flatten schedule's built-in
/// defaults), configuration can override or extend them, symbol matching is case-insensitive, and — the safety
/// property — an unknown instrument is an <b>explicit miss</b>, never a fabricated default. A wrong tick size does
/// not fail loudly; it silently mis-sizes every risk calculation downstream, so there is no safe default to invent.
/// </remarks>
public class InstrumentSpecOptionsTests
{
    private static InstrumentSpecSource Source(InstrumentSpecOptions? options = null) =>
        new(Options.Create(options ?? new InstrumentSpecOptions()));

    private static InstrumentId Symbol(string symbol) => InstrumentId.Parse(symbol);

    // ---- built-in defaults: the products the flatten schedule already governs ----

    [Theory]
    [InlineData("ES", 0.25, 50)]
    [InlineData("NQ", 0.25, 20)]
    [InlineData("CL", 0.01, 1000)]
    [InlineData("GC", 0.10, 100)]
    public void TryResolve_ShouldReturnTheBuiltInSpec_ForAKnownProduct(string symbol, double tickSize, double pointValue)
    {
        bool resolved = Source().TryResolve(Symbol(symbol), out InstrumentContractSpec? spec);

        resolved.Should().BeTrue();
        spec!.Spec.TickSize.Should().Be((decimal)tickSize);
        spec.Spec.PointValue.Should().Be((decimal)pointValue);
        spec.SafetyStopTicks.Should().BePositive();
    }

    [Fact]
    public void TryResolve_ShouldMatchTheSymbolCaseInsensitively()
    {
        Source().TryResolve(Symbol("es"), out InstrumentContractSpec? spec).Should().BeTrue();

        spec!.Spec.TickSize.Should().Be(0.25m);
    }

    // ---- the safety property: a miss is explicit, never a fabricated default ----

    [Fact]
    public void TryResolve_ShouldReportAnExplicitMiss_ForAnUnconfiguredInstrument()
    {
        // A fabricated tick size does not fail loudly -- it silently mis-sizes every downstream risk calculation.
        // Refusing is the only safe answer, so the take path can fail closed rather than guess.
        bool resolved = Source().TryResolve(Symbol("ZZ"), out InstrumentContractSpec? spec);

        resolved.Should().BeFalse();
        spec.Should().BeNull();
    }

    // ---- configuration overrides and extends, mirroring the flatten schedule ----

    [Fact]
    public void TryResolve_ShouldPreferAConfiguredOverride_OverTheBuiltInDefault()
    {
        InstrumentSpecOptions options = new()
        {
            Instruments = [new InstrumentSpecOption { Symbol = "ES", TickSize = 0.5m, PointValue = 25m, SafetyStopTicks = 40 }],
        };

        Source(options).TryResolve(Symbol("ES"), out InstrumentContractSpec? spec).Should().BeTrue();

        spec!.Spec.TickSize.Should().Be(0.5m);
        spec.Spec.PointValue.Should().Be(25m);
        spec.SafetyStopTicks.Should().Be(40);
    }

    [Fact]
    public void TryResolve_ShouldResolveAConfiguredInstrument_ThatIsNotABuiltInDefault()
    {
        InstrumentSpecOptions options = new()
        {
            Instruments = [new InstrumentSpecOption { Symbol = "RTY", TickSize = 0.1m, PointValue = 50m, SafetyStopTicks = 30 }],
        };

        Source(options).TryResolve(Symbol("RTY"), out InstrumentContractSpec? spec).Should().BeTrue();

        spec!.Spec.PointValue.Should().Be(50m);
    }

    [Fact]
    public void SafetyStopDistance_ShouldBeTicksTimesTickSize()
    {
        Source().TryResolve(Symbol("ES"), out InstrumentContractSpec? spec);

        // The safety stop is carried as a DISTANCE, because a price is only meaningful against a specific entry.
        spec!.SafetyStopDistance.Should().Be(spec.SafetyStopTicks * spec.Spec.TickSize);
    }

    // ---- startup validation: a bad spec must not start the host ----

    [Theory]
    [InlineData(0, 50, 20)]      // non-positive tick size
    [InlineData(-0.25, 50, 20)]
    [InlineData(0.25, 0, 20)]    // non-positive point value
    [InlineData(0.25, -50, 20)]
    [InlineData(0.25, 50, 0)]    // non-positive safety-stop distance
    [InlineData(0.25, 50, -5)]
    public void Validate_ShouldRejectANonPositiveField(double tickSize, double pointValue, int safetyStopTicks)
    {
        InstrumentSpecOptions options = new()
        {
            Instruments =
            [
                new InstrumentSpecOption
                {
                    Symbol = "ES",
                    TickSize = (decimal)tickSize,
                    PointValue = (decimal)pointValue,
                    SafetyStopTicks = safetyStopTicks,
                },
            ],
        };

        options.Validate().Should().BeFalse();
    }

    [Fact]
    public void Validate_ShouldRejectAnEntryWithNoSymbol()
    {
        InstrumentSpecOptions options = new()
        {
            Instruments = [new InstrumentSpecOption { Symbol = "  ", TickSize = 0.25m, PointValue = 50m, SafetyStopTicks = 20 }],
        };

        options.Validate().Should().BeFalse();
    }

    [Fact]
    public void Validate_ShouldAcceptTheDefaultAndAWellFormedEntry()
    {
        new InstrumentSpecOptions().Validate().Should().BeTrue();

        InstrumentSpecOptions configured = new()
        {
            Instruments = [new InstrumentSpecOption { Symbol = "ES", TickSize = 0.25m, PointValue = 50m, SafetyStopTicks = 20 }],
        };

        configured.Validate().Should().BeTrue();
    }
}
