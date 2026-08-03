using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain.Venue;
using Scalar.AspNetCore;

namespace MarqSpec.TradingCopilot.Api.Documentation;

/// <summary>
/// Wires the self-documenting API surface (gh#604, R-10): an OpenAPI 3 document generated from the actual
/// minimal-API routes, and a Scalar reference UI over it. A spec built from the routes is correct <b>by
/// construction</b>, which the hand-maintained README endpoint table (that this replaces, #605) never was.
/// </summary>
public static class OpenApiRegistration
{
    /// <summary>The single OpenAPI document name — served at <c>/openapi/v1.json</c>, browsed at <c>/scalar/v1</c>.</summary>
    public const string DocumentName = "v1";

    /// <summary>
    /// Registers the OpenAPI document generator, its title/version, and the <c>Bearer</c> security scheme plus the
    /// per-operation requirement — both derived from the endpoints' own authorization metadata
    /// (<see cref="BearerSecurity"/>), so the spec cannot claim an endpoint is open when its route requires a
    /// token, or the reverse.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddTradingCopilotOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info.Title = "Trading Co-Pilot API";
                document.Info.Version = DocumentName;
                document.Info.Description =
                    "The single-operator futures trading co-pilot BFF (R-10). Generated from the live minimal-API "
                    + "routes (gh#604) — the source of truth the README links to, replacing the hand-kept table.";
                return Task.CompletedTask;
            });
            options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
            options.AddOperationTransformer<BearerSecurityRequirementTransformer>();
        });

        return services;
    }

    /// <summary>
    /// Maps the OpenAPI JSON endpoint (<c>/openapi/v1.json</c>, <b>always</b>) and — everywhere except production —
    /// the interactive Scalar reference UI (<c>/scalar/v1</c>). The JSON is safe to serve everywhere: it merely
    /// describes the surface, and every write endpoint still requires a JWT. The interactive "try it" console is
    /// gated by <see cref="ScalarUiPolicy"/> so a browsable trigger for real orders / the kill switch never ships
    /// to the live venue (gh#604).
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The same application, for chaining.</returns>
    public static WebApplication MapTradingCopilotApiReference(this WebApplication app)
    {
        // Explicitly AllowAnonymous, on the record (R-18, gh#604). The spec + UI expose only the API SHAPE — never
        // data or an action, every one of which still requires a JWT — and that shape is already public (the README,
        // #605). Marking them anonymous rather than leaving them ungated makes their reachability a decision the
        // authorization-surface sweep can see and sanction, not an omission it must flag.
        app.MapOpenApi().AllowAnonymous();

        DeploymentEnvironment environment = app.Services.GetRequiredService<HostTradingEnvironment>().Value;
        if (ScalarUiPolicy.IsEnabledFor(environment))
        {
            app.MapScalarApiReference().AllowAnonymous();
        }

        return app;
    }
}
