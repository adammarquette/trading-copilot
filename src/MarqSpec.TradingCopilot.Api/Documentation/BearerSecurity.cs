using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MarqSpec.TradingCopilot.Api.Documentation;

/// <summary>
/// The <c>Bearer</c> (JWT) security derivation for the generated OpenAPI document (gh#604): the scheme id and the
/// pure rule for whether a given endpoint carries the security requirement. The rule reads the endpoint's own
/// authorization metadata rather than a hand-kept list, so the spec's security can never drift out of step with
/// the routes — the "correct by construction" property the README endpoint table never had.
/// </summary>
internal static class BearerSecurity
{
    /// <summary>The scheme id, declared in components and referenced by each secured operation.</summary>
    internal const string SchemeName = "Bearer";

    /// <summary>
    /// Whether an endpoint with this metadata must advertise the Bearer requirement in the spec. True only when the
    /// endpoint requires authorization <b>and</b> is not explicitly anonymous — <see cref="IAllowAnonymous"/> wins,
    /// mirroring ASP.NET Core's own precedence, so a group that <c>RequireAuthorization()</c>s but exposes one
    /// <c>AllowAnonymous</c> route (e.g. <c>/auth/login</c>) documents that route as open, exactly as it behaves.
    /// </summary>
    /// <param name="endpointMetadata">The endpoint's metadata collection (authorization attributes included).</param>
    /// <returns><see langword="true"/> if the operation should require Bearer auth in the spec.</returns>
    internal static bool RequiresBearer(IEnumerable<object> endpointMetadata)
    {
        bool allowsAnonymous = endpointMetadata.OfType<IAllowAnonymous>().Any();
        bool requiresAuthorization = endpointMetadata.OfType<IAuthorizeData>().Any();
        return requiresAuthorization && !allowsAnonymous;
    }

    /// <summary>
    /// Builds the operation-level Bearer security requirement, keyed by a reference to the scheme declared in
    /// <paramref name="hostDocument"/>'s components.
    /// </summary>
    /// <remarks>
    /// <b>The host document is required, and that is the whole point of this method existing (gh#680).</b> An
    /// <see cref="OpenApiSecuritySchemeReference"/> built without one cannot resolve its target, so the serializer
    /// writes <b>no key at all</b> and the requirement collapses to <c>{}</c> — which in OpenAPI means "no security
    /// required". The document then declares a <c>Bearer</c> scheme that nothing references, and every client
    /// generator reads the whole API as open: the exact inversion of what gh#604 set out to express, and a
    /// documentation defect that survived review once already because <c>{}</c> looks like an empty-but-present
    /// entry rather than a wrong one.
    /// </remarks>
    /// <param name="hostDocument">The document being generated — the reference resolves the scheme against it.</param>
    /// <returns>A requirement that serializes as <c>{ "Bearer": [] }</c>.</returns>
    internal static OpenApiSecurityRequirement CreateRequirement(OpenApiDocument hostDocument) =>
        new()
        {
            [new OpenApiSecuritySchemeReference(SchemeName, hostDocument)] = new List<string>(),
        };
}

/// <summary>
/// Declares the <c>Bearer</c> (JWT) security scheme once, in the document's components (gh#604). The app's auth is
/// mandatory — <c>Program</c> refuses to start without a JWT signing key — so the scheme is advertised
/// unconditionally rather than gated on runtime auth configuration.
/// </summary>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[BearerSecurity.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            In = ParameterLocation.Header,
            BearerFormat = "JWT",
            Description = "JWT issued by POST /auth/login, sent as: Authorization: Bearer {token}.",
        };

        return Task.CompletedTask;
    }
}

/// <summary>
/// Adds the <c>Bearer</c> security requirement to each operation whose endpoint requires authorization — and to no
/// other (gh#604) — using <see cref="BearerSecurity.RequiresBearer"/> so the requirement tracks the route's real
/// authorization metadata instead of a restated list.
/// </summary>
internal sealed class BearerSecurityRequirementTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (BearerSecurity.RequiresBearer(context.Description.ActionDescriptor.EndpointMetadata))
        {
            // Fail loudly rather than emit a requirement we cannot name (gh#680). The generator always supplies the
            // document, so this is unreachable in practice -- but the two silent alternatives are both a lie the
            // document would then carry: passing null reproduces the original `{}` ("no security required"), and
            // skipping the requirement documents a gated endpoint as open. A broken /openapi/v1.json is visible and
            // costs nothing; a spec that quietly advertises the order and kill-switch endpoints as open is what this
            // issue existed to stop. Nothing about enforcement rides on this path -- R-18 is enforced by the routes.
            OpenApiDocument document = context.Document
                ?? throw new InvalidOperationException(
                    "The OpenAPI operation transformer was given no host document, so the Bearer security "
                    + "requirement cannot reference the scheme it requires (gh#680).");

            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(BearerSecurity.CreateRequirement(document));
        }

        return Task.CompletedTask;
    }
}
