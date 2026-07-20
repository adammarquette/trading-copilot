using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.AspNetCore.Http;

namespace MarqSpec.TradingCopilot.Api.Auth;

/// <summary>
/// The server-side <see cref="ICurrentUser"/> — reads the user id from the JWT on the current request, wiring the
/// data layer's per-user isolation (R-20 / ADR-0011) to the authenticated principal. Unauthenticated ⇒
/// <see cref="Guid.Empty"/> (default-deny).
/// </summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    /// <inheritdoc />
    public Guid UserId => ClaimsPrincipalUserId.Resolve(httpContextAccessor.HttpContext?.User);
}
