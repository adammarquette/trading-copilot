using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MarqSpec.TradingCopilot.Api.Auth;

/// <summary>Reads the authenticated user's id from a principal's claims. Kept free of ASP.NET types so it is unit-testable.</summary>
public static class ClaimsPrincipalUserId
{
    /// <summary>Resolves the user id from the <c>sub</c> (or name-identifier) claim.</summary>
    /// <param name="principal">The current principal, or <see langword="null"/>.</param>
    /// <returns>The user id, or <see cref="Guid.Empty"/> when unauthenticated / unparseable.</returns>
    public static Guid Resolve(ClaimsPrincipal? principal)
    {
        string? subject = principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(subject, out Guid userId) ? userId : Guid.Empty;
    }
}
