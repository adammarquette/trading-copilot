using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.ProjectX;

/// <summary>
/// Resolving a ProjectX account's stage from its name. The whole point is what it <b>refuses</b> to classify:
/// a wrong guess here is what gh#60 removed — a misread funded account traded with real money at stake.
/// </summary>
public class ProjectXAccountStageTests
{
    [Theory]
    [InlineData("PRAC-50K-1234")]
    [InlineData("PRAC-V2-0000-10000001")]
    [InlineData("PRAC")]
    [InlineData("prac-50k-1234")] // case-insensitive
    public void Resolve_ShouldBePractice_WhenThePracMarkerIsTheFirstSegment(string name)
    {
        ProjectXAccountStage.Resolve(name).Should().Be(AccountStage.Practice);
    }

    [Theory]
    [InlineData("50KTC-V2-DLL-0000")]     // funded/eval — genuinely ambiguous, not guessed
    [InlineData("EXPRESS-50K-9003")]      // Apex-style funded naming
    [InlineData("PRACTICE-CONVERTED-50K")] // NOT the "PRAC" segment — must not read as practice
    [InlineData("SOMETHING-UNFAMILIAR")]
    public void Resolve_ShouldBeUnknown_WhenTheNameIsNotConfidentlyPractice(string name)
    {
        // Unknown is the safe answer: it resolves to Undeclared and is tradeable nowhere until the operator
        // classifies it. Funded/evaluation naming varies by firm and is deferred, not guessed (gh#76).
        ProjectXAccountStage.Resolve(name).Should().Be(AccountStage.Unknown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Resolve_ShouldBeUnknown_WhenTheNameIsMissing(string? name)
    {
        ProjectXAccountStage.Resolve(name).Should().Be(AccountStage.Unknown);
    }
}
