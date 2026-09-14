using MarqSpec.TradingCopilot.Api.Documentation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

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

    /// <summary>
    /// The gh#680 regression guard. <see cref="BearerSecurity.RequiresBearer"/> decides <i>whether</i> an operation
    /// carries the requirement; this pins <i>what the requirement says</i> once serialized — the half that was
    /// silently broken from gh#604 until gh#612's drift-guard caught it.
    /// </summary>
    [Fact]
    public void CreateRequirement_ShouldNameTheBearerScheme_WhenSerialized()
    {
        // The failure this guards is not an exception or a missing entry — it is a requirement that serializes as
        // `{}`, which in OpenAPI means "NO security required". A scheme reference built without a host document
        // cannot resolve its target, so the serializer emits no key and the document ends up advertising the whole
        // API as open while still declaring a Bearer scheme nothing points at. Asserting on the object graph would
        // miss it entirely: the reference exists either way. Only the SERIALIZED form can witness this.
        OpenApiDocument document = HostDocumentDeclaringTheBearerScheme();

        OpenApiSecurityRequirement requirement = BearerSecurity.CreateRequirement(document);

        string json = Serialize(requirement);
        json.Should().Contain(
            BearerSecurity.SchemeName,
            "the requirement must NAME the scheme it requires — an empty `{{}}` requirement means 'no security "
            + "required' in OpenAPI, so every generated client would read the API as open (gh#680)");
        json.Should().NotBe(
            "{ }",
            "an empty requirement object is the exact defect gh#680 fixed — see the comment above");
    }

    /// <summary>A minimal document declaring the Bearer scheme, as <c>BearerSecuritySchemeTransformer</c> does.</summary>
    private static OpenApiDocument HostDocumentDeclaringTheBearerScheme() =>
        new()
        {
            Info = new OpenApiInfo { Title = "Trading Co-Pilot", Version = "v1" },
            Components = new OpenApiComponents
            {
                SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    [BearerSecurity.SchemeName] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                    },
                },
            },
        };

    private static string Serialize(OpenApiSecurityRequirement requirement)
    {
        StringWriter text = new();
        OpenApiJsonWriter writer = new(text);
        requirement.SerializeAsV31(writer);
        text.Flush();
        return text.ToString();
    }
}
