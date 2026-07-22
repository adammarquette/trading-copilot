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
