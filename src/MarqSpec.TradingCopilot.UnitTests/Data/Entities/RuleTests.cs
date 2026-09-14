using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;

namespace MarqSpec.TradingCopilot.UnitTests.Data.Entities;

/// <summary>
/// The durable rulebook <see cref="Rule"/> row (gh#866, R-7, data dictionary §8) — entity invariants and the
/// inert-until-confirmed default. A newly constructed rule must never read as live: <c>Enabled</c> and
/// <c>Confirmed</c> default false, and <see cref="Rule.IsArmed"/> is true only when both are deliberately set.
/// </summary>
public class RuleTests
{
    [Fact]
    public void ANewRule_ShouldDefaultInert_WhenOnlyRequiredFieldsAreSet()
    {
        Rule rule = NewRule();

        rule.Enabled.Should().BeFalse("a rule is never silently live — Enabled defaults off");
        rule.Confirmed.Should().BeFalse("a rule is never silently live — Confirmed defaults off");
        rule.NeedsRevalidation.Should().BeFalse("nothing has changed since authorship, so the flag starts clean");
        rule.IsArmed().Should().BeFalse("inert-until-confirmed: authorship arms nothing");
    }

    [Fact]
    public void IsArmed_ShouldBeFalse_WhenEnabledButNotConfirmed()
    {
        Rule rule = NewRule();
        rule.Enabled = true;

        rule.IsArmed().Should().BeFalse(
            "Enabled is the live/paused switch; confirmation is the separate gate that accepts the rule (gh#866)");
    }

    [Fact]
    public void IsArmed_ShouldBeFalse_WhenConfirmedButNotEnabled()
    {
        Rule rule = NewRule();
        rule.Confirmed = true;

        rule.IsArmed().Should().BeFalse("a confirmed-but-paused rule is not live");
    }

    [Fact]
    public void IsArmed_ShouldBeTrue_WhenEnabledAndConfirmed()
    {
        Rule rule = NewRule();
        rule.Enabled = true;
        rule.Confirmed = true;

        rule.IsArmed().Should().BeTrue("only the operator's two deliberate switches together arm a rule");
    }

    [Fact]
    public void EmbeddingOwnerId_ShouldBeTheRuleId_SoTheVecRowAddressesThisOwner()
    {
        Guid id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Rule rule = NewRule();
        rule.Id = id;

        rule.EmbeddingOwnerId.Should().Be(
            id.ToString("D"),
            "§8's VEC half is EmbeddingRecord under EmbeddingOwnerKind.Rule; OwnerId is this row's id");
    }

    [Fact]
    public void Rule_ShouldBeOperatorOwned_SoTheR20FilterApplies()
    {
        typeof(IUserOwned).IsAssignableFrom(typeof(Rule))
            .Should().BeTrue("the ERD's Operator owns Rule — an unscoped rulebook is an R-20 leak");
    }

    private static Rule NewRule() => new()
    {
        IntentText = "Fade an RSI extreme on ES",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };
}
