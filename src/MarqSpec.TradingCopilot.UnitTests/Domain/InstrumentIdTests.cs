using MarqSpec.TradingCopilot.Domain;

namespace MarqSpec.TradingCopilot.UnitTests.Domain;

public class InstrumentIdTests
{
    [Theory]
    [InlineData("es", "ES")]
    [InlineData("  nq ", "NQ")]
    [InlineData("cl", "CL")]
    public void Parse_ShouldNormalizeSymbol_WhenValueHasCaseOrWhitespace(string input, string expected)
    {
        InstrumentId id = InstrumentId.Parse(input);

        id.Symbol.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_ShouldThrowFormatException_WhenValueIsBlank(string? input)
    {
        Action act = () => InstrumentId.Parse(input);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void TryParse_ShouldReturnFalseAndDefault_WhenValueIsBlank()
    {
        bool parsed = InstrumentId.TryParse("   ", out InstrumentId id);

        parsed.Should().BeFalse();
        id.Should().Be(default(InstrumentId));
    }

    [Fact]
    public void Equality_ShouldHold_WhenSymbolsMatchAfterNormalization()
    {
        InstrumentId.Parse("es").Should().Be(InstrumentId.Parse(" ES "));
    }
}
