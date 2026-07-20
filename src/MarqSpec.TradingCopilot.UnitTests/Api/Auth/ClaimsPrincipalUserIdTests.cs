using System.Security.Claims;
using MarqSpec.TradingCopilot.Api.Auth;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Auth;

public class ClaimsPrincipalUserIdTests
{
    [Fact]
    public void Resolve_ShouldReturnTheSubClaimAsGuid()
    {
        Guid id = Guid.NewGuid();
        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, id.ToString())], "test"));

        ClaimsPrincipalUserId.Resolve(principal).Should().Be(id);
    }

    [Fact]
    public void Resolve_ShouldReturnEmpty_WhenThereIsNoPrincipal()
    {
        ClaimsPrincipalUserId.Resolve(null).Should().Be(Guid.Empty);
    }

    [Fact]
    public void Resolve_ShouldReturnEmpty_WhenThereIsNoSubjectClaim()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity());

        ClaimsPrincipalUserId.Resolve(principal).Should().Be(Guid.Empty);
    }
}
