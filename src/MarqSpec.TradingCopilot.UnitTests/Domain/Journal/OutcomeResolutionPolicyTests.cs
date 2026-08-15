using MarqSpec.TradingCopilot.Domain.Journal;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Journal;

public class OutcomeResolutionPolicyTests
{
    // --- A closed trade resolves win / loss / scratch from the SIGN of its realized P&L ---

    [Fact]
    public void TryResolve_ShouldBeWin_WhenAClosedTradeRealizedPositive()
    {
        OutcomeResolutionPolicy.TryResolve(OutcomeBasis.ClosedTrade, 125.50m, out OutcomeResolution resolution)
            .Should().BeTrue();

        resolution.Should().Be(OutcomeResolution.Win);
    }

    [Fact]
    public void TryResolve_ShouldBeLoss_WhenAClosedTradeRealizedNegative()
    {
        OutcomeResolutionPolicy.TryResolve(OutcomeBasis.ClosedTrade, -80m, out OutcomeResolution resolution)
            .Should().BeTrue();

        resolution.Should().Be(OutcomeResolution.Loss);
    }

    [Fact]
    public void TryResolve_ShouldBeScratch_WhenAClosedTradeRoundTrippedExactlyFlat()
    {
        // A filled round trip that netted exactly zero is a scratch (no net result) -- documented at the enum.
        OutcomeResolutionPolicy.TryResolve(OutcomeBasis.ClosedTrade, 0m, out OutcomeResolution resolution)
            .Should().BeTrue();

        resolution.Should().Be(OutcomeResolution.NoFillScratch);
    }

    [Fact]
    public void TryResolve_ShouldRefuse_WhenAClosedTradeCarriesNoRealizedResult()
    {
        // Refuse-don't-guess: a closed-trade basis with no signed P&L is unresolvable -- an open or unknown trade,
        // never silently classified as a scratch.
        OutcomeResolutionPolicy.TryResolve(OutcomeBasis.ClosedTrade, null, out OutcomeResolution resolution)
            .Should().BeFalse();

        resolution.Should().Be(OutcomeResolution.Unknown);
    }

    // --- An untaken suggestion resolves from its terminal disposition, P&L irrelevant ---

    [Fact]
    public void TryResolve_ShouldBeExpired_WhenAnUnfilledSuggestionRanOutItsWindow()
    {
        OutcomeResolutionPolicy.TryResolve(OutcomeBasis.ExpiredUnfilled, null, out OutcomeResolution resolution)
            .Should().BeTrue();

        resolution.Should().Be(OutcomeResolution.Expired);
    }

    [Fact]
    public void TryResolve_ShouldBeScratch_WhenAnUnfilledSuggestionWasPassedBeforeExpiry()
    {
        OutcomeResolutionPolicy.TryResolve(OutcomeBasis.Scratched, null, out OutcomeResolution resolution)
            .Should().BeTrue();

        resolution.Should().Be(OutcomeResolution.NoFillScratch);
    }

    [Fact]
    public void TryResolve_ShouldRefuse_WhenTheBasisIsNotADefinedValue()
    {
        // A bad cast / corrupt basis cannot be classified -- exhaustive over the enum, refuse the rest.
        OutcomeResolutionPolicy.TryResolve((OutcomeBasis)99, 10m, out OutcomeResolution resolution)
            .Should().BeFalse();

        resolution.Should().Be(OutcomeResolution.Unknown);
    }
}
