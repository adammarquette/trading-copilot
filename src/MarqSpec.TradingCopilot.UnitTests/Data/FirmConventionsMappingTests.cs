using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Data;

/// <summary>
/// The bridge from a persisted <see cref="Firm"/> to the domain <see cref="FirmConventions"/> — the point where
/// the operator's stored declarations become the value object the venue adapter resolves modes against (gh#76).
/// </summary>
public class FirmConventionsMappingTests
{
    private static Firm FirmWith(string name, params (AccountStage Stage, bool AtRisk)[] declarations)
    {
        return new Firm
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Name = name,
            Type = FirmType.PropFirm,
            StageConventions =
            [
                .. declarations.Select(d => new FirmStageConvention { Stage = d.Stage, CapitalAtRisk = d.AtRisk }),
            ],
        };
    }

    [Fact]
    public void ToConventions_ShouldResolveABrokerageAccountToSomethingOtherThanUndeclared()
    {
        // gh#780's acceptance, stated as the card states it. A brokerage carries NO stage declarations — it has no
        // evaluation/funded ladder, its account names resolve to AccountStage.Unknown, and FirmConventions.For
        // refuses to declare Unknown — so the per-stage model resolved every discovered brokerage account to
        // Undeclared: refused in every environment, production included, with nothing the operator could declare
        // to fix it. The bridge now routes a brokerage to the venue-flag form instead.
        Firm brokerage = new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Name = "Interactive Brokers",
            Type = FirmType.Brokerage,
            StageConventions = [],
        };

        FirmConventions conventions = brokerage.ToConventions();

        conventions.ModeFor(AccountStage.Unknown, venueReportsSimulated: true)
            .Should().Be(TradingMode.Practice, "a brokerage paper account IS practice");
        conventions.ModeFor(AccountStage.Unknown, venueReportsSimulated: false)
            .Should().NotBe(TradingMode.Undeclared, "gh#780: a discovered brokerage account must be tradeable somewhere");
        conventions.ModeFor(AccountStage.Unknown, venueReportsSimulated: false)
            .Should().Be(TradingMode.Live);
    }

    [Fact]
    public void ToConventions_ShouldKeepAPropFirmOnItsDeclarations_NotTheVenueFlag()
    {
        // The R-14 half. A funded prop account reports `simulated` at the venue and is nonetheless Live, so the
        // brokerage branch must not reach a prop firm — that leak would resolve a real-payout account to Practice.
        Firm propFirm = FirmWith("Topstep", (AccountStage.Funded, true));

        propFirm.ToConventions().ModeFor(AccountStage.Funded, venueReportsSimulated: true)
            .Should().Be(TradingMode.Live);
    }

    [Fact]
    public void ToConventions_ShouldResolveEachDeclaredStageToItsMode()
    {
        Firm firm = FirmWith(
            "Topstep",
            (AccountStage.Practice, false),
            (AccountStage.Evaluation, false),
            (AccountStage.Funded, true));

        FirmConventions conventions = firm.ToConventions();

        conventions.Firm.Should().Be("Topstep");
        conventions.ModeFor(AccountStage.Practice).Should().Be(TradingMode.Practice);
        conventions.ModeFor(AccountStage.Evaluation).Should().Be(TradingMode.Practice);
        conventions.ModeFor(AccountStage.Funded).Should().Be(TradingMode.Live);
    }

    [Fact]
    public void ToConventions_ShouldLeaveAnUndeclaredStageUndeclared()
    {
        Firm firm = FirmWith("Apex", (AccountStage.Evaluation, false));

        firm.ToConventions().ModeFor(AccountStage.Funded).Should().Be(TradingMode.Undeclared);
    }

    [Fact]
    public void ToConventions_ShouldLeaveEverythingUndeclared_WhenNothingIsDeclared()
    {
        Firm firm = FirmWith("Fresh");

        firm.ToConventions().ModeFor(AccountStage.Funded).Should().Be(TradingMode.Undeclared);
    }
}
