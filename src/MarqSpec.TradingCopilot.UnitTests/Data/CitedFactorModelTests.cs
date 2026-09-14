using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MarqSpec.TradingCopilot.UnitTests.Data;

/// <summary>
/// The cited-factor set's schema half (gh#729, ADR-0026, R-4): a <see cref="CitedFactor"/> is a child of its
/// <see cref="Suggestion"/> (cascade FK), operator-owned so the R-20 default-deny filter applies, and the DB pins
/// the invariants the in-memory provider cannot — a <b>partial unique index</b> giving each suggestion at most one
/// primary, plus the fail-closed <c>Kind</c>/<c>TimeframeMinutes</c> checks and the kind/columns arm pairing. These
/// are relational-model concepts, so they are asserted from the model metadata (built offline, never connected).
/// </summary>
public class CitedFactorModelTests
{
    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    // Build the model against the RELATIONAL provider without connecting. UseVector matches production (gh#109):
    // without it EmbeddingRecord.Embedding has no mapping and the model itself will not build.
    private static TradingCopilotDbContext RelationalModel() => new(
        new DbContextOptionsBuilder<TradingCopilotDbContext>()
            .UseNpgsql("Host=not-connected;Database=model-only", npgsql => npgsql.UseVector())
            .Options,
        new FixedUser(Guid.Empty));

    private static IEntityType CitedFactorType()
    {
        TradingCopilotDbContext relational = RelationalModel();
        return relational.Model.FindEntityType(typeof(CitedFactor))!;
    }

    [Fact]
    public void CitedFactor_ShouldBeOperatorOwned_SoTheR20FilterApplies()
    {
        typeof(IUserOwned).IsAssignableFrom(typeof(CitedFactor))
            .Should().BeTrue("a cited factor is a child of an owned suggestion — it must carry the R-20 owner");

        // Registered in the model as an owned entity — the TenantDbContext then gives every IUserOwned type the
        // default-deny per-user filter (asserted end-to-end by DataLayerScopingTests).
        CitedFactorType().Should().NotBeNull();
    }

    [Fact]
    public void CitedFactor_ShouldCascadeFromItsSuggestion()
    {
        IEntityType factor = CitedFactorType();

        IForeignKey suggestionFk = factor.GetForeignKeys()
            .Single(fk => fk.Properties.Any(property => property.Name == nameof(CitedFactor.SuggestionId)));

        suggestionFk.PrincipalEntityType.ClrType.Should().Be(typeof(Suggestion), "a factor belongs to a suggestion");
        suggestionFk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade, "factors do not outlive their suggestion");
    }

    [Fact]
    public void CitedFactor_ShouldAllowAtMostOnePrimary_PerSuggestion()
    {
        IIndex onePrimary = CitedFactorType().GetIndexes()
            .Single(index => index.Name == "UX_SuggestionCitedFactors_OnePrimary");

        onePrimary.IsUnique.Should().BeTrue("a suggestion has exactly one primary factor (ADR-0026)");
        onePrimary.Properties.Select(property => property.Name).Should().Equal([nameof(CitedFactor.SuggestionId)]);
        onePrimary.GetFilter().Should().Contain("IsPrimary",
            "the uniqueness is scoped to the primary rows — supporting factors are unconstrained in number");
    }

    // Check constraints are design-time-only metadata — they are stripped from the read-optimized runtime model
    // (relational.Model), so they must be read from the design-time model.
    private static ICheckConstraint Check(string name) =>
        RelationalModel().GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(CitedFactor))!
            .GetCheckConstraints()
            .Single(constraint => constraint.Name == name);

    [Fact]
    public void CitedFactor_ShouldRefuseTheUnknownKind_AndANonPositiveTimeframe()
    {
        Check("CK_SuggestionCitedFactors_Kind_NotUnknown").Sql.Should().Contain("\"Kind\" <> 0");
        Check("CK_SuggestionCitedFactors_Timeframe_Positive").Sql.Should().Contain("\"TimeframeMinutes\" > 0");
    }

    [Fact]
    public void CitedFactor_ShouldPairKindToItsArmColumns()
    {
        // R-4 immutability + a well-formed arm: an Indicator (1) fills the indicator columns and nulls the level
        // snapshot; a Level (2) does the reverse. The pairing check refuses a half-built row written past the model.
        string pairing = Check("CK_SuggestionCitedFactors_KindColumns").Sql!;

        pairing.Should().Contain("\"Indicator\"").And.Contain("\"LevelTop\"",
            "the pairing constrains both arms against the kind");
    }
}
