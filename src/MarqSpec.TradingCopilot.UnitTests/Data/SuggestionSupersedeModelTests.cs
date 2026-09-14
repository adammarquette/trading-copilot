using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MarqSpec.TradingCopilot.UnitTests.Data;

/// <summary>
/// The supersede spine's schema half (gh#550, R-4): <see cref="Suggestion.SupersedesId"/> is a self-referencing FK
/// with <c>OnDelete: Restrict</c> so a superseded row can never be silently deleted out from under the row that
/// supersedes it, and <see cref="Suggestion.Version"/> defaults to 1. Both are DB concepts the in-memory provider
/// does not enforce, so they are asserted from the <b>relational</b> model metadata (built offline, never connected).
/// </summary>
public class SuggestionSupersedeModelTests
{
    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    // Build the model against the RELATIONAL provider without connecting. UseVector matches production (gh#109):
    // without it EmbeddingRecord.Embedding has no mapping and the model itself will not build.
    private static TradingCopilotDbContext RelationalModel() => new(
        new DbContextOptionsBuilder<TradingCopilotDbContext>()
            .UseNpgsql("Host=not-connected;Database=model-only", npgsql => npgsql.UseVector())
            .Options,
        new FixedUser(Guid.Empty));

    [Fact]
    public void Suggestion_ShouldSelfReferenceSupersedesId_WithRestrictOnDelete()
    {
        using TradingCopilotDbContext relational = RelationalModel();
        IEntityType suggestion = relational.Model.FindEntityType(typeof(Suggestion))!;

        IForeignKey selfFk = suggestion.GetForeignKeys()
            .Single(fk => fk.Properties.Any(property => property.Name == nameof(Suggestion.SupersedesId)));

        selfFk.PrincipalEntityType.ClrType.Should().Be(typeof(Suggestion), "the supersede link is a self-reference");
        selfFk.DeleteBehavior.Should().Be(DeleteBehavior.Restrict, "history can never silently vanish (gh#550)");
        selfFk.Properties.Should().ContainSingle()
            .Which.IsNullable.Should().BeTrue("the first version supersedes nothing");
    }

    [Fact]
    public void Suggestion_VersionColumn_ShouldBeNonNullableAndDefaultToOne()
    {
        using TradingCopilotDbContext relational = RelationalModel();
        IProperty version = relational.Model.FindEntityType(typeof(Suggestion))!
            .FindProperty(nameof(Suggestion.Version))!;

        version.IsNullable.Should().BeFalse();
        version.GetDefaultValue().Should().Be(1, "an un-superseded suggestion is version 1 (gh#550)");
    }
}
