using MarqSpec.TradingCopilot.Domain.Suggestions;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Suggestions;

/// <summary>
/// The pure primary-selection rule (gh#729, ADR-0026, R-4): a suggestion cites a SET of factors with exactly one
/// <b>primary</b> and zero-or-more supporting, and the primary is the single <b>smallest-timeframe</b> factor
/// (gh#592's min rule). The N=1 "set of one" is the degenerate case every suggestion is today — that one factor is
/// its own primary. Issuance derives <see cref="Data.Entities.CitedFactor.IsPrimary"/> through here rather than
/// hand-setting it, so these cases pin the rule off the database.
/// </summary>
public class CitedFactorSetTests
{
    // A minimal payload carrying only what the rule reads — its timeframe — plus a tag to assert identity.
    private sealed record Factor(string Tag, int TimeframeMinutes);

    private static IReadOnlyList<CitedFactorPrimary<Factor>> Derive(params Factor[] factors) =>
        CitedFactorSet.DerivePrimary(factors, factor => factor.TimeframeMinutes);

    [Fact]
    public void DerivePrimary_ShouldFlagTheSmallestTimeframe_AsPrimary()
    {
        // gh#592 min-rule: across 60/15/5, the 5-minute read is the headline; the larger ones are supporting.
        IReadOnlyList<CitedFactorPrimary<Factor>> ranked = Derive(
            new Factor("h1", 60), new Factor("m15", 15), new Factor("m5", 5));

        ranked.Single(r => r.IsPrimary).Factor.Tag.Should().Be("m5", "the smallest timeframe is the headline");
        ranked.Where(r => !r.IsPrimary).Select(r => r.Factor.Tag).Should().BeEquivalentTo(["h1", "m15"]);
    }

    [Fact]
    public void DerivePrimary_ShouldMakeTheLoneFactorPrimary_ForASetOfOne()
    {
        // The N=1 degenerate case — exactly today's single-cited-signal behaviour.
        IReadOnlyList<CitedFactorPrimary<Factor>> ranked = Derive(new Factor("only", 5));

        ranked.Should().ContainSingle().Which.IsPrimary.Should().BeTrue("a set of one is its own primary");
    }

    [Fact]
    public void DerivePrimary_ShouldFlagExactlyOnePrimary_Always()
    {
        IReadOnlyList<CitedFactorPrimary<Factor>> ranked = Derive(
            new Factor("a", 15), new Factor("b", 5), new Factor("c", 30), new Factor("d", 60));

        ranked.Count(r => r.IsPrimary).Should().Be(1, "a suggestion has exactly one primary factor (ADR-0026)");
    }

    [Fact]
    public void DerivePrimary_ShouldBreakTiesToTheFirst_SoExactlyOneIsPrimary()
    {
        // Two factors share the smallest timeframe. The rule is deterministic — the first in sequence wins — so the
        // "exactly one primary" invariant (the DB's partial unique index) can never be violated by a tie.
        IReadOnlyList<CitedFactorPrimary<Factor>> ranked = Derive(
            new Factor("first5", 5), new Factor("second5", 5), new Factor("m15", 15));

        ranked.Count(r => r.IsPrimary).Should().Be(1);
        ranked.Single(r => r.IsPrimary).Factor.Tag.Should().Be("first5", "ties break to the first in sequence");
    }

    [Fact]
    public void DerivePrimary_ShouldPreserveInputOrder()
    {
        // Ranking must not reorder the set — issuance zips the flags back onto the factors it passed in.
        IReadOnlyList<CitedFactorPrimary<Factor>> ranked = Derive(
            new Factor("a", 60), new Factor("b", 5), new Factor("c", 15));

        ranked.Select(r => r.Factor.Tag).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void DerivePrimary_ShouldReturnEmpty_ForAnEmptySet()
    {
        CitedFactorSet.DerivePrimary(Array.Empty<Factor>(), factor => factor.TimeframeMinutes)
            .Should().BeEmpty("no factors, no primary");
    }
}
