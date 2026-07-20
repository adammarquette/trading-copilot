using MarqSpec.TradingCopilot.Api.Auth;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Auth;

public class InvitationTokensTests
{
    [Fact]
    public void Generate_ShouldReturnDistinctTokens()
    {
        (string first, _) = InvitationTokens.Generate();
        (string second, _) = InvitationTokens.Generate();

        first.Should().NotBe(second);
    }

    [Fact]
    public void Generate_ShouldReturnAHashThatMatchesHashingTheToken()
    {
        (string token, string tokenHash) = InvitationTokens.Generate();

        InvitationTokens.Hash(token).Should().Be(tokenHash);
    }

    [Fact]
    public void Hash_ShouldBeDeterministic()
    {
        InvitationTokens.Hash("some-token").Should().Be(InvitationTokens.Hash("some-token"));
    }

    [Fact]
    public void Hash_ShouldDifferForDifferentTokens()
    {
        InvitationTokens.Hash("token-a").Should().NotBe(InvitationTokens.Hash("token-b"));
    }
}
