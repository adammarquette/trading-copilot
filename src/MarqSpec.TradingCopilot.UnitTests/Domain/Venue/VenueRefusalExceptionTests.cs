using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

/// <summary>
/// The venue-neutral refusal exception (gh#629): it carries the <see cref="VenueRefusalKind"/> a catch site needs to
/// tell a definitive rejection from an indeterminate fault. The one behaviour that matters for safety: an
/// un-classified refusal must default to <see cref="VenueRefusalKind.Indeterminate"/> (assume the order may be live),
/// never definitive.
/// </summary>
public class VenueRefusalExceptionTests
{
    [Fact]
    public void Kind_ShouldDefaultToIndeterminate_WhenNotClassified()
    {
        // The fail-safe default: an unrecognised / unset refusal is treated as maybe-live, never auto-resolved. The
        // enum's zero value backs this so `default` is safe too.
        new VenueRefusalException("something went wrong").Kind.Should().Be(VenueRefusalKind.Indeterminate);
        default(VenueRefusalKind).Should().Be(VenueRefusalKind.Indeterminate);
    }

    [Theory]
    [InlineData(VenueRefusalKind.Definitive)]
    [InlineData(VenueRefusalKind.Indeterminate)]
    public void Kind_ShouldCarryTheClassification_WhenGiven(VenueRefusalKind kind)
    {
        VenueRefusalException refusal = new("the venue said no", kind, errorCode: 42);

        refusal.Kind.Should().Be(kind);
        refusal.ErrorCode.Should().Be(42);
        refusal.Message.Should().Be("the venue said no");
    }
}
