using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MarqSpec.TradingCopilot.UnitTests.Data.Entities;

/// <summary>
/// The relational model half of gh#866: <see cref="TriggerRecord.SourceRuleId"/> stays a soft reference (no FK),
/// so deleting a <see cref="Rule"/> cannot cascade onto a trigger, and <see cref="Rule.SourceConversationId"/>
/// is the same shape as the trigger's conversation seam.
/// </summary>
public class RuleModelTests
{
    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private static TradingCopilotDbContext RelationalModel() => new(
        new DbContextOptionsBuilder<TradingCopilotDbContext>()
            .UseNpgsql("Host=not-connected;Database=model-only", npgsql => npgsql.UseVector())
            .Options,
        new FixedUser(Guid.Empty));

    [Fact]
    public void TriggerSourceRuleId_ShouldHaveNoForeignKey_SoTheTriggerOutlivesTheRule()
    {
        using TradingCopilotDbContext relational = RelationalModel();
        IEntityType trigger = relational.Model.FindEntityType(typeof(TriggerRecord))!;

        trigger.GetForeignKeys()
            .Where(fk => fk.Properties.Any(property => property.Name == nameof(TriggerRecord.SourceRuleId)))
            .Should().BeEmpty(
                "SourceRuleId is a soft reference (gh#471 / gh#866): an FK would couple the trigger's lifetime to the rule");
    }

    [Fact]
    public void RuleSourceConversationId_ShouldHaveNoForeignKey_MatchingTheTriggerSeam()
    {
        using TradingCopilotDbContext relational = RelationalModel();
        IEntityType rule = relational.Model.FindEntityType(typeof(Rule))!;

        rule.GetForeignKeys()
            .Where(fk => fk.Properties.Any(property => property.Name == nameof(Rule.SourceConversationId)))
            .Should().BeEmpty(
                "source conversation is a soft reference, same as TriggerRecord.SourceConversationId");
    }

    [Fact]
    public void Rule_ShouldBeMapped_AsAnOperatorOwnedEntity()
    {
        using TradingCopilotDbContext relational = RelationalModel();

        relational.Model.FindEntityType(typeof(Rule)).Should().NotBeNull("the Rule row is the §8 REL half");
        typeof(IUserOwned).IsAssignableFrom(typeof(Rule)).Should().BeTrue();
    }

    [Fact]
    public void EmbeddingOwnerKind_Rule_ShouldBeTheVecOwnerForARuleRow()
    {
        ((int)EmbeddingOwnerKind.Rule).Should().Be(
            3, "owner kind 3 was reserved for the rulebook row (gh#109); gh#866 is the producer that fills it");
    }
}
