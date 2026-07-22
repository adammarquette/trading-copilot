using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.ProjectX;

/// <summary>
/// Resolving a ProjectX account's stage from its name. The patterns are <b>grounded in a real Topstep roster</b>
/// (gh#76: 294 accounts, six name families, zero unmatched) — and the point is still what the resolver
/// <b>refuses</b> to classify: a wrong guess is what gh#60 removed. Anything outside the grounded families
/// stays <see cref="AccountStage.Unknown"/>.
/// </summary>
public class ProjectXAccountStageTests
{
    [Theory]
    [InlineData("PRAC-50K-1234")]
    [InlineData("PRAC-V2-0000-10000001")]
    [InlineData("PRAC")]
    [InlineData("prac-50k-1234")]          // case-insensitive
    [InlineData("PRACTICEJUL214923016")]   // legacy single-segment practice: PRACTICE + month + digits
    [InlineData("practicefeb1010650842")]  // case-insensitive
    public void Resolve_ShouldBePractice_ForThePracticeFamilies(string name)
    {
        ProjectXAccountStage.Resolve(name).Should().Be(AccountStage.Practice);
    }

    [Theory]
    [InlineData("50KTC-V2-0000-10000001")]  // modern Trading Combine: <size>KTC first segment
    [InlineData("100KTC-V2-DLL-0000-1")]
    [InlineData("150ktc-v2-0000-1")]        // case-insensitive
    [InlineData("S1JUL1015039217")]         // legacy step evaluation: S + step digit + month + digits
    [InlineData("s1feb1210725810")]         // case-insensitive
    public void Resolve_ShouldBeEvaluation_ForTheTradingCombineFamilies(string name)
    {
        ProjectXAccountStage.Resolve(name).Should().Be(AccountStage.Evaluation);
    }

    [Theory]
    [InlineData("EXPRESS-V2-0000-10000001")]     // modern Express Funded Account
    [InlineData("EXPRESS-V2-CT-DLL-0000-1")]
    [InlineData("express-v2-0000-1")]            // case-insensitive
    [InlineData("EXPRESSJUL1015027963")]         // legacy single-segment funded: EXPRESS + month + digits
    public void Resolve_ShouldBeFunded_ForTheExpressFamilies(string name)
    {
        // Direction-of-error check: misreading something AS funded is the over-cautious direction (funded x
        // capital-at-risk conventions -> Live -> maximally guarded). The dangerous direction -- funded misread
        // as practice/eval -- is what the Unknown fallback below protects against.
        ProjectXAccountStage.Resolve(name).Should().Be(AccountStage.Funded);
    }

    [Theory]
    [InlineData("PRACTICE-CONVERTED-50K")] // hyphenated PRACTICE is NOT the legacy shape and NOT the PRAC marker
    [InlineData("SOMETHING-UNFAMILIAR")]
    [InlineData("KTC-V2-0000")]            // no size digits before KTC -- not the combine marker
    [InlineData("XKTC-V2-0000")]           // non-digit prefix -- not the combine marker
    [InlineData("S1JUL")]                  // legacy shapes need trailing digits...
    [InlineData("SJUL1234")]               // ...and the step digit...
    [InlineData("S1XYZ1234")]              // ...and a real month
    [InlineData("EXPRESSJULY")]            // month then digits, not arbitrary text
    [InlineData("PRACTICEX1234")]          // not a month
    public void Resolve_ShouldBeUnknown_WhenTheNameIsOutsideTheGroundedFamilies(string name)
    {
        // Unknown is the safe answer: it resolves to Undeclared and is tradeable nowhere until the operator
        // classifies it (the per-account override, gh#76). Near-misses stay Unknown on purpose -- the patterns
        // are anchored, not fuzzy, because a wrong guess is what gh#60 removed.
        ProjectXAccountStage.Resolve(name).Should().Be(AccountStage.Unknown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Resolve_ShouldBeUnknown_WhenTheNameIsMissing(string? name)
    {
        ProjectXAccountStage.Resolve(name).Should().Be(AccountStage.Unknown);
    }
}
