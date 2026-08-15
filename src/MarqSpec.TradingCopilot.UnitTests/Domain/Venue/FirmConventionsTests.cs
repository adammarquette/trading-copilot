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

    // --- Brokerages resolve from the venue's routing flag (gh#780) ---

    [Fact]
    public void ModeFor_ShouldFollowTheVenueFlag_WhenTheFirmIsABrokerage()
    {
        // gh#780. At a BROKERAGE the venue's flag is honest: a paper account IS practice and a live account IS
        // live. R-14's "never derive mode from the venue flag" exists because a prop-firm FUNDED account executes
        // on a simulated engine while real payout is at stake — that argument does not reach a brokerage, and
        // extending it there left every discovered brokerage account permanently Undeclared, i.e. untradeable
        // everywhere with no way for the operator to fix it.
        FirmConventions brokerage = FirmConventions.ForBrokerage("Interactive Brokers");

        brokerage.ModeFor(AccountStage.Unknown, venueReportsSimulated: true).Should().Be(TradingMode.Practice);
        brokerage.ModeFor(AccountStage.Unknown, venueReportsSimulated: false).Should().Be(TradingMode.Live);
    }

    [Fact]
    public void ModeFor_ShouldIgnoreTheVenueFlag_WhenTheFirmIsAPropFirm()
    {
        // The other half of the same rule, and the one that must not regress: a funded prop account reports
        // `simulated` at the venue and is nonetheless Live. If the brokerage branch ever leaked into this path it
        // would resolve a real-payout account to Practice — the exact inversion R-14 exists to prevent.
        Topstep().ModeFor(AccountStage.Funded, venueReportsSimulated: true).Should().Be(TradingMode.Live);
        Topstep().ModeFor(AccountStage.Evaluation, venueReportsSimulated: false).Should().Be(TradingMode.Practice);
    }

    [Fact]
    public void ModeFor_ShouldStayUndeclared_WhenABrokerageStageIsResolvedWithoutTheVenueFlag()
    {
        // The stage-only overload cannot answer for a brokerage — it has no flag to read — so it fails closed
        // rather than guessing. Callers that can supply the flag use the two-argument form.
        FirmConventions.ForBrokerage("Interactive Brokers").ModeFor(AccountStage.Unknown)
            .Should().Be(TradingMode.Undeclared);
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

    // --- What may be declared at all ---

    [Fact]
    public void For_ShouldRejectADeclarationAgainstUnknown()
    {
        // Unknown means "we could not read the stage", so declaring what it means is incoherent. Accepting it
        // silently would be worse than refusing: ModeFor ignores Unknown, so the operator would believe they
        // had classified something that still resolves to Undeclared.
        Action act = () => FirmConventions.For("Topstep", (AccountStage.Unknown, false));

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("Unknown");
    }

    [Fact]
    public void For_ShouldRejectAStageThatIsNotADefinedValue()
    {
        // Config or a cast can produce a value outside the enum. Storing it would let ModeFor hand back
        // Practice or Live for a stage the domain does not recognise -- an unknown thing becoming tradeable,
        // which is the exact failure direction this type exists to prevent.
        Action act = () => FirmConventions.For("Topstep", ((AccountStage)99, false));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ModeFor_ShouldBeUndeclared_WhenTheStageIsNotADefinedValue()
    {
        Topstep().ModeFor((AccountStage)99).Should().Be(TradingMode.Undeclared);
    }

    [Fact]
    public void IsDeclared_ShouldBeFalse_WhenTheStageIsNotADefinedValue()
    {
        Topstep().IsDeclared((AccountStage)99).Should().BeFalse();
    }
}
