using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Auth;

/// <summary>The <c>/auth</c> endpoints: login (issue a JWT) and me (the current user).</summary>
public static class AuthEndpoints
{
    /// <summary>Maps the auth endpoints.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/auth");
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapGet("/me", MeAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        TradingCopilotDbContext database,
        IPasswordHasher passwordHasher,
        ITokenIssuer tokenIssuer,
        CancellationToken cancellationToken)
    {
        User? user = await database.Users
            .FirstOrDefaultAsync(u => u.Email == request.Email, cancellationToken);

        // A single 401 for both "no such user" and "wrong password" — never reveal which.
        if (user is null || !passwordHasher.Verify(user.PasswordHash, request.Password))
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new { token = tokenIssuer.Issue(user) });
    }

    private static async Task<IResult> MeAsync(
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        User? user = await database.Users
            .FirstOrDefaultAsync(u => u.Id == currentUser.UserId, cancellationToken);

        return user is null
            ? Results.NotFound()
            : Results.Ok(new { user.Id, user.Email, user.DisplayName });
    }
}
