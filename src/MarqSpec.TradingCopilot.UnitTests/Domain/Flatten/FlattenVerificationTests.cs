using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Flatten;

/// <summary>
/// Firing the close is not the same as being flat. R-13 requires the system to verify, then retry or escalate
/// loudly — so the input here is what the <b>venue</b> reports afterwards, never what we believe we did.
/// </summary>
public class FlattenVerificationTests
{
    private static VenueId Venue => VenueId.Parse("projectx");

    private static PositionSnapshot Position(int netQuantity)
    {
        return new PositionSnapshot(
            VenueAccountId.Create(Venue, "9001"),
            VenueContractId.Create(Venue, "CON.F.US.EP.U26"),
            netQuantity,
            new Price(5_000m));
    }

    [Fact]
    public void Verify_ShouldBeFlat_WhenTheVenueReportsNothingOpen()
    {
        FlattenVerification.Verify([], attemptsMade: 1, maxAttempts: 3).Should().Be(FlattenVerdict.Flat);
    }

    [Fact]
    public void Verify_ShouldBeFlat_WhenEveryRemainingPositionIsZeroed()
    {
        // The venue may still list the contract with a zero net quantity rather than dropping the row.
        FlattenVerification.Verify([Position(0)], attemptsMade: 1, maxAttempts: 3)
            .Should().Be(FlattenVerdict.Flat);
    }

    [Fact]
    public void Verify_ShouldRetry_WhenExposureRemainsAndAttemptsAreLeft()
    {
        FlattenVerification.Verify([Position(2)], attemptsMade: 1, maxAttempts: 3)
            .Should().Be(FlattenVerdict.Retry);
    }

    [Fact]
    public void Verify_ShouldEscalate_WhenExposureRemainsAndAttemptsAreExhausted()
    {
        // Loudly, per R-13 -- a position that survives the flatten is the case the whole feature exists to
        // prevent, so it must never end in a silent give-up.
        FlattenVerification.Verify([Position(2)], attemptsMade: 3, maxAttempts: 3)
            .Should().Be(FlattenVerdict.Escalate);
    }

    [Fact]
    public void Verify_ShouldEscalate_WhenAShortPositionSurvives()
    {
        // Direction is irrelevant: any non-zero net quantity is exposure carried into the close.
        FlattenVerification.Verify([Position(-1)], attemptsMade: 3, maxAttempts: 3)
            .Should().Be(FlattenVerdict.Escalate);
    }

    [Fact]
    public void Verify_ShouldEscalate_WhenAnyOneOfSeveralPositionsSurvives()
    {
        FlattenVerification.Verify([Position(0), Position(1), Position(0)], attemptsMade: 3, maxAttempts: 3)
            .Should().Be(FlattenVerdict.Escalate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Verify_ShouldThrow_WhenMaxAttemptsIsNotPositive(int maxAttempts)
    {
        // Zero attempts would mean "never retry, never escalate" -- a configuration that silently disables the
        // safety net.
        Action act = () => FlattenVerification.Verify([Position(1)], attemptsMade: 1, maxAttempts);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
