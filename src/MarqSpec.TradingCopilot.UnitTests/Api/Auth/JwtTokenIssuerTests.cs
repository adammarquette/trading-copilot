using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Auth;

public class JwtTokenIssuerTests
{
    [Fact]
    public void Issue_ShouldEmitATokenCarryingTheUserIdEmailAndIssuer()
    {
        JwtOptions options = new()
        {
            SigningKey = "unit-test-signing-key-that-is-long-enough-for-hs256",
            Issuer = "test-issuer",
            Audience = "test-audience",
            LifetimeMinutes = 30,
        };
        JwtTokenIssuer issuer = new(Options.Create(options));
        User user = new() { Id = Guid.NewGuid(), Email = "trader@example.com", PasswordHash = "x", DisplayName = "Trader" };

        string token = issuer.Issue(user);

        JsonWebToken parsed = new JsonWebTokenHandler().ReadJsonWebToken(token);
        parsed.GetClaim(JwtRegisteredClaimNames.Sub).Value.Should().Be(user.Id.ToString());
        parsed.GetClaim(JwtRegisteredClaimNames.Email).Value.Should().Be(user.Email);
        parsed.Issuer.Should().Be("test-issuer");
    }
}
