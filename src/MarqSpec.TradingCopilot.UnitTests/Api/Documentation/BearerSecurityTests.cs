using MarqSpec.TradingCopilot.Api.Documentation;
using Microsoft.AspNetCore.Authorization;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Documentation;

/// <summary>
/// The Bearer security requirement in the generated spec is derived from each endpoint's <b>own</b> authorization
/// metadata (gh#604), not a hand-kept list — so it cannot drift out of step with the route the way the README
/// endpoint table could. These pin that derivation, including the precedence case that a hand-written list would
/// get wrong: a group that <c>RequireAuthorization()</c>s but exposes one <c>AllowAnonymous</c> route
/// (e.g. <c>/auth/login</c>) documents that route as <b>open</b>, exactly as ASP.NET Core serves it.
/// </summary>
public class BearerSecurityTests
{
    [Fact]
    public void RequiresBearer_ShouldBeTrue_WhenTheEndpointRequiresAuthorization()
    {
        object[] metadata = [new AuthorizeAttribute()];

        BearerSecurity.RequiresBearer(metadata).Should().BeTrue(
            "an authorized endpoint must advertise the Bearer requirement in the spec");
    }

    [Fact]
    public void RequiresBearer_ShouldBeFalse_WhenTheEndpointIsAnonymous()
    {
        // The /auth/login shape: anonymous, no authorization requirement.
        object[] metadata = [new AllowAnonymousAttribute()];

        BearerSecurity.RequiresBearer(metadata).Should().BeFalse();
    }

    [Fact]
    public void RequiresBearer_ShouldBeFalse_WhenAnonymousOverridesAGroupRequirement()
    {
        // The precedence case a restated list gets wrong: the group RequireAuthorization()s (an AuthorizeAttribute
        // is present) but the route opts out with AllowAnonymous. Anonymous wins in ASP.NET Core, so the spec must
        // document the route as open, not secured.
        object[] metadata = [new AuthorizeAttribute(), new AllowAnonymousAttribute()];

        BearerSecurity.RequiresBearer(metadata).Should().BeFalse(
            "AllowAnonymous wins over an inherited authorization requirement, and the spec must match the route");
    }

    [Fact]
    public void RequiresBearer_ShouldBeFalse_WhenThereIsNoAuthorizationMetadata()
    {
        BearerSecurity.RequiresBearer([]).Should().BeFalse();
    }
}
