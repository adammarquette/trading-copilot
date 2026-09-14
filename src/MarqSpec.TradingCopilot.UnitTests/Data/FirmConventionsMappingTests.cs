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

    /// <summary>A persisted account carrying just the two inputs the mode recompute reads.</summary>
    private static Account AccountWith(AccountStage stage, bool? venueReportsSimulated) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        ConnectionId = Guid.NewGuid(),
        VenueAccountKey = "9001",
        Name = "ACC-9001",
        Stage = stage,
        VenueReportsSimulated = venueReportsSimulated,
    };

    private static Firm Brokerage(string name) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Name = name,
        Type = FirmType.Brokerage,
        StageConventions = [],
    };

    [Fact]
    public void ToConventions_ShouldResolveABrokerageAccountToSomethingOtherThanUndeclared()
    {
        // gh#780's acceptance, stated as the card states it. A brokerage carries NO stage declarations — it has no
        // evaluation/funded ladder, its account names resolve to AccountStage.Unknown, and FirmConventions.For
        // refuses to declare Unknown — so the per-stage model resolved every discovered brokerage account to
        // Undeclared: refused in every environment, production included, with nothing the operator could declare
        // to fix it. The bridge now routes a brokerage to the venue-flag form instead.
        FirmConventions conventions = Brokerage("Interactive Brokers").ToConventions();

        conventions.ModeFor(AccountStage.Unknown, venueReportsSimulated: true)
            .Should().Be(TradingMode.Practice, "a brokerage paper account IS practice");
        conventions.ModeFor(AccountStage.Unknown, venueReportsSimulated: false)
            .Should().NotBe(TradingMode.Undeclared, "gh#780: a discovered brokerage account must be tradeable somewhere");
        conventions.ModeFor(AccountStage.Unknown, venueReportsSimulated: false)
            .Should().Be(TradingMode.Live);
    }

    [Fact]
    public void RecomputeMode_ShouldResolveABrokerageFromTheStoredVenueFlag_NotBackToUndeclared()
    {
        // #907 review. Resolving mode correctly in FirmConventions was not enough: the value the rest of the app
        // reads is the PERSISTED Account.Mode, and every write point recomputes it through RecomputeMode. That
        // used the stage-only overload, which for a brokerage returns Undeclared by design -- so discovery
        // computed the right mode on the VenueAccount and then immediately overwrote the persisted column back to
        // Undeclared. TradeableHere, the DB-level mode guard and order placement all read that column.
        FirmConventions conventions = Brokerage("Interactive Brokers").ToConventions();

        Account live = AccountWith(AccountStage.Unknown, venueReportsSimulated: false);
        Account paper = AccountWith(AccountStage.Unknown, venueReportsSimulated: true);

        live.RecomputeMode(conventions);
        paper.RecomputeMode(conventions);

        live.Mode.Should().Be(TradingMode.Live);
        paper.Mode.Should().Be(TradingMode.Practice);
    }

    [Fact]
    public void RecomputeMode_ShouldStayUndeclared_WhenTheVenueFlagHasNeverBeenObserved()
    {
        // "Never observed" is not "not simulated". A row predating the column — or one whose venue never reported
        // the flag — must not become LIVE-tradeable off a default nobody read; it stays refused everywhere until a
        // discovery fills it in, the same declared-unknown posture as the mode itself.
        Account unobserved = AccountWith(AccountStage.Unknown, venueReportsSimulated: null);

        unobserved.RecomputeMode(Brokerage("Interactive Brokers").ToConventions());

        unobserved.Mode.Should().Be(TradingMode.Undeclared);
    }

    [Fact]
    public void RecomputeMode_ShouldIgnoreTheStoredVenueFlag_AtAPropFirm()
    {
        // The R-14 half at the persistence layer: a funded prop account reports `simulated` at the venue and is
        // Live regardless. If the brokerage branch leaked here it would persist Practice on a real-payout account.
        FirmConventions conventions = FirmWith("Topstep", (AccountStage.Funded, true)).ToConventions();
        Account funded = AccountWith(AccountStage.Funded, venueReportsSimulated: true);

        funded.RecomputeMode(conventions);

        funded.Mode.Should().Be(TradingMode.Live);
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
