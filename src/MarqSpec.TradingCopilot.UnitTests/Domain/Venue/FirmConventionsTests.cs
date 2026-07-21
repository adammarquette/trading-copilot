using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

/// <summary>
/// What a stage <i>means at a given firm</i> — the operator's declaration, because no venue reports it.
/// A venue flag says where an order executes; only the operator knows whether capital is at stake.
/// </summary>
public class FirmConventionsTests
{
    private static FirmConventions Topstep()
    {
        // Evaluation is a paid attempt; funded carries a real payout, so a breach costs money.
        return FirmConventions.For(
            "Topstep",
            (AccountStage.Practice, false),
            (AccountStage.Evaluation, false),
            (AccountStage.Funded, true));
    }

    [Fact]
    public void ModeFor_ShouldBePractice_WhenTheFirmDeclaresTheStageCarriesNoCapital()
    {
        Topstep().ModeFor(AccountStage.Evaluation).Should().Be(TradingMode.Practice);
    }

    [Fact]
    public void ModeFor_ShouldBeLive_WhenTheFirmDeclaresCapitalIsAtRisk()
    {
        // A funded prop account executes on a simulated engine yet a breach costs a real payout. That is the
        // case the venue flag gets wrong, and the whole reason this declaration exists.
        Topstep().ModeFor(AccountStage.Funded).Should().Be(TradingMode.Live);
    }

    [Fact]
    public void ModeFor_ShouldBeUndeclared_WhenTheStageWasNeverClassified()
    {
        FirmConventions partial = FirmConventions.For("Apex", (AccountStage.Evaluation, false));

        partial.ModeFor(AccountStage.Funded).Should().Be(TradingMode.Undeclared);
    }

    [Fact]
    public void ModeFor_ShouldBeUndeclared_WhenNothingHasBeenDeclaredAtAll()
    {
        FirmConventions.None.ModeFor(AccountStage.Funded).Should().Be(TradingMode.Undeclared);
    }

    [Fact]
    public void ModeFor_ShouldBeUndeclared_WhenTheStageItselfCouldNotBeIdentified()
    {
        // You cannot classify what you could not read. An unrecognised account name must not inherit whatever
        // the firm happened to declare for something else.
        Topstep().ModeFor(AccountStage.Unknown).Should().Be(TradingMode.Undeclared);
    }

    [Fact]
    public void IsDeclared_ShouldDistinguishAnExplicitPracticeFromSilence()
    {
        // "Declared as practice" and "never declared" both avoid Live -- but only one may be traded.
        FirmConventions topstep = Topstep();

        topstep.IsDeclared(AccountStage.Evaluation).Should().BeTrue();
        topstep.IsDeclared(AccountStage.Unknown).Should().BeFalse();
    }

    [Fact]
    public void For_ShouldRejectADuplicateDeclarationForTheSameStage()
    {
        // Two answers for one stage is an ambiguous safety input, not a merge.
        Action act = () => FirmConventions.For(
            "Topstep", (AccountStage.Funded, true), (AccountStage.Funded, false));

        act.Should().Throw<ArgumentException>();
    }
}
