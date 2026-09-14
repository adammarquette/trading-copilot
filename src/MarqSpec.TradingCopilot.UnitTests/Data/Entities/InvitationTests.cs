using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.UnitTests.Data.Entities;

public class InvitationTests
{
    [Fact]
    public void CanBeAcceptedAt_ShouldBeTrue_WhenPendingAndNotExpired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Pending(expiresUtc: now.AddDays(1)).CanBeAcceptedAt(now).Should().BeTrue();
    }

    [Fact]
    public void CanBeAcceptedAt_ShouldBeFalse_WhenExpired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Pending(expiresUtc: now.AddSeconds(-1)).CanBeAcceptedAt(now).Should().BeFalse();
    }

    [Theory]
    [InlineData(InvitationStatus.Accepted)]
    [InlineData(InvitationStatus.Revoked)]
    [InlineData(InvitationStatus.Expired)]
    public void CanBeAcceptedAt_ShouldBeFalse_WhenNotPending(InvitationStatus status)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Invitation invitation = Pending(expiresUtc: now.AddDays(1));
        invitation.Status = status;

        invitation.CanBeAcceptedAt(now).Should().BeFalse();
    }

    private static Invitation Pending(DateTimeOffset expiresUtc)
    {
        return new Invitation
        {
            Email = "invitee@example.com",
            TokenHash = "hash",
            Status = InvitationStatus.Pending,
            ExpiresUtc = expiresUtc,
        };
    }
}
