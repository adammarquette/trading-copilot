using System.Security.Claims;
using MarqSpec.TradingCopilot.Api.Auth;
using Microsoft.AspNetCore.SignalR;

namespace MarqSpec.TradingCopilot.Api.Realtime;

/// <summary>
/// Keys a SignalR connection to its owning operator for <c>Clients.User</c> routing (gh#683, R-20). SignalR's default
/// <see cref="IUserIdProvider"/> reads the <c>ClaimTypes.NameIdentifier</c> claim, but tokens carry the id under
/// <c>sub</c> with <c>MapInboundClaims = false</c>, so the default would match nobody. This resolves the same id every
/// HTTP request does (<see cref="ClaimsPrincipalUserId"/>), and returns <see langword="null"/> for an unresolved
/// identity so an unauthenticated / malformed connection is grouped under no operator and receives no owner-scoped
/// push (fail-closed).
/// </summary>
internal sealed class RealtimeUserIdProvider : IUserIdProvider
{
    /// <inheritdoc />
    public string? GetUserId(HubConnectionContext connection) => ToUserId(connection.User);

    /// <summary>The <c>Clients.User</c> key for a principal — its owner id, or <see langword="null"/> when the
    /// identity does not resolve (fail-closed). Pure, so it is unit-tested without a live connection.</summary>
    internal static string? ToUserId(ClaimsPrincipal? user) =>
        ClaimsPrincipalUserId.Resolve(user) is var userId && userId != Guid.Empty ? userId.ToString() : null;
}
