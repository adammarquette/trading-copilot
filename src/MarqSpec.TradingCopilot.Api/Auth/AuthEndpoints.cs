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
        group.MapPost("/invitations", IssueInvitationAsync).RequireAuthorization();
        group.MapPost("/accept-invite", AcceptInviteAsync).AllowAnonymous();
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

    private static async Task<IResult> IssueInvitationAsync(
        IssueInvitationRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        (string token, string tokenHash) = InvitationTokens.Generate();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Invitation invitation = new()
        {
            Id = Guid.NewGuid(),
            Email = request.Email,
            TokenHash = tokenHash,
            IssuedByUserId = currentUser.UserId,
            Status = InvitationStatus.Pending,
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(7),
        };

        database.Invitations.Add(invitation);
        await database.SaveChangesAsync(cancellationToken);

        // The plaintext token is returned once — only its hash is stored.
        return Results.Ok(new { invitation.Id, token, expiresUtc = invitation.ExpiresUtc });
    }

    private static async Task<IResult> AcceptInviteAsync(
        AcceptInviteRequest request,
        TradingCopilotDbContext database,
        IPasswordHasher passwordHasher,
        ITokenIssuer tokenIssuer,
        CancellationToken cancellationToken)
    {
        string tokenHash = InvitationTokens.Hash(request.Token);
        Invitation? invitation = await database.Invitations
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (invitation is null || !invitation.CanBeAcceptedAt(now))
        {
            return Results.BadRequest(new { error = "This invitation is invalid, already used, or expired." });
        }

        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = invitation.Email,
            PasswordHash = passwordHasher.Hash(request.Password),
            DisplayName = request.DisplayName,
            Status = UserStatus.Active,
            CreatedUtc = now,
        };

        database.Users.Add(user);
        invitation.Status = InvitationStatus.Accepted;
        invitation.AcceptedByUserId = user.Id;
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { token = tokenIssuer.Issue(user) });
    }
}
