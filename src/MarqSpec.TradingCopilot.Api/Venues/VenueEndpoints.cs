using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace MarqSpec.TradingCopilot.Api.Venues;

/// <summary>
/// The <c>/venues</c> endpoints — a venue's setup-time self-description (ADR-0023, gh#64), so onboarding renders the
/// labelled credential schema from the adapter rather than a hardcoded form. Read-only, authenticated, and
/// value-free: it serves the <em>schema</em> (keys, labels, secret/required flags, help), never a credential.
/// </summary>
public static class VenueEndpoints
{
    /// <summary>Maps the venue endpoints. All require authentication (R-18).</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapVenueEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/venues").RequireAuthorization().WithTags("Venues");
        group.MapGet("/setup", GetVenueSetup)
            .WithSummary("List each wired venue's onboarding setup contract — identity and labelled credential schema.");
        return endpoints;
    }

    /// <summary>
    /// Serves the registered setup contracts, resolved from DI — so a venue appears the moment its contract is
    /// registered at the composition root (compile-time discovery, ADR-0023 §2). Schema only: no credential value
    /// ever leaves here (ADR-0015).
    /// </summary>
    /// <param name="contracts">The compiled-in setup contracts (all registered <see cref="IVenueSetupContract"/>).</param>
    /// <returns>200 with the contracts projected to value-free DTOs.</returns>
    internal static IResult GetVenueSetup([FromServices] IEnumerable<IVenueSetupContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        return Results.Ok(contracts.Select(VenueSetupResponse.From).ToList());
    }
}
