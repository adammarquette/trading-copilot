using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Venues;

/// <summary>
/// The <c>/connections</c> endpoints — firm logins (ADR-0016) and account discovery through them. This is where
/// the #60 arc closes: a discovered account's mode comes from the firm's <b>declared</b> conventions, resolved
/// through the venue adapter, never from the venue's own flag.
/// </summary>
public static class ConnectionEndpoints
{
    /// <summary>The platform keys with a wired adapter. Everything else is refused at creation, loudly.</summary>
    private static IReadOnlySet<string> WiredPlatforms { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "projectx" };

    /// <summary>Maps the connection endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapConnectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/connections").RequireAuthorization();
        group.MapPost("/", CreateConnectionAsync);
        group.MapGet("/", ListConnectionsAsync);
        group.MapPost("/{id:guid}/accounts/discover", DiscoverAccountsAsync);
        return endpoints;
    }

    internal static async Task<IResult> CreateConnectionAsync(
        CreateConnectionRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CredentialKey))
        {
            return Results.BadRequest(new { error = "A credential key is required — the env-entry name, never the credentials." });
        }

        if (!WiredPlatforms.Contains(request.Platform ?? string.Empty))
        {
            // Shown-as-unavailable beats silently-accepted (ADR-0016): a connection on an unwired platform
            // could never discover or trade, and storing it would only defer the surprise.
            return Results.BadRequest(new { error = $"Platform '{request.Platform}' has no wired adapter. Wired: {string.Join(", ", WiredPlatforms)}." });
        }

        bool firmExists = await database.Firms.AnyAsync(firm => firm.Id == request.FirmId, cancellationToken);
        if (!firmExists)
        {
            return Results.NotFound(new { error = "No such firm in this workspace." });
        }

        // One login per firm x platform (ADR-0016). The DB unique index is the backstop.
        bool duplicate = await database.Connections.AnyAsync(
            connection => connection.FirmId == request.FirmId && connection.Platform == request.Platform,
            cancellationToken);
        if (duplicate)
        {
            return Results.Conflict(new { error = "A connection for this firm on this platform already exists — one login per firm × platform." });
        }

        Connection created = new()
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId,
            FirmId = request.FirmId,
            Platform = request.Platform!.ToLowerInvariant(),
            CredentialKey = request.CredentialKey,
        };

        database.Connections.Add(created);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(ConnectionResponse.From(created));
    }

    internal static async Task<IResult> ListConnectionsAsync(
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        List<Connection> connections = await database.Connections.ToListAsync(cancellationToken);

        return Results.Ok(connections.Select(ConnectionResponse.From).ToList());
    }

    internal static async Task<IResult> DiscoverAccountsAsync(
        Guid id,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        CancellationToken cancellationToken)
    {
        Connection? connection = await database.Connections
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (connection is null)
        {
            return Results.NotFound();
        }

        // One credential set per process (the client's websocket is a singleton -- ADR-0015). Serving a
        // connection whose credentials this process does NOT hold would silently discover someone else's login;
        // refuse with the mismatch spelled out instead.
        string configuredKey = projectXOptions.Value.CredentialKey;
        if (!string.Equals(connection.CredentialKey, configuredKey, StringComparison.Ordinal))
        {
            return Results.Conflict(new
            {
                error = $"This process holds credentials for key '{configuredKey}', not '{connection.CredentialKey}'. "
                    + "One ProjectX credential set per process (ADR-0015); reconfigure or run a process for that key.",
            });
        }

        Firm? firm = await database.Firms
            .Include(candidate => candidate.StageConventions)
            .FirstOrDefaultAsync(candidate => candidate.Id == connection.FirmId, cancellationToken);
        if (firm is null)
        {
            return Results.NotFound(new { error = "The connection's firm no longer exists." });
        }

        FirmConventions conventions = firm.ToConventions();
        ITradingVenue venue = venueFactory.Create(conventions);

        IReadOnlyList<VenueAccount> discovered;
        try
        {
            discovered = await venue.GetAccountsAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The venue is an upstream gateway: bad credentials, an unreachable host, or a gateway error should
            // read as exactly that -- not as this API crashing.
            return Results.Problem(
                title: "The venue refused or failed the account discovery.",
                detail: error.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }

        // Upsert by (connection, venue key): rediscovery refreshes what the venue reports -- name, stage,
        // tradability, balance -- and never duplicates (the DB unique index is the backstop).
        Dictionary<string, Account> existing = await database.Accounts
            .Where(account => account.ConnectionId == connection.Id)
            .ToDictionaryAsync(account => account.VenueAccountKey, cancellationToken);

        List<DiscoveredAccountResponse> responses = [];
        foreach (VenueAccount venueAccount in discovered)
        {
            if (!existing.TryGetValue(venueAccount.Id.Key, out Account? account))
            {
                account = new Account
                {
                    Id = Guid.NewGuid(),
                    UserId = currentUser.UserId,
                    ConnectionId = connection.Id,
                    VenueAccountKey = venueAccount.Id.Key,
                    Name = venueAccount.Name,
                };
                database.Accounts.Add(account);
            }

            account.Name = venueAccount.Name;
            account.Stage = venueAccount.Stage;
            account.CanTrade = venueAccount.CanTrade;
            account.IsVisible = venueAccount.IsVisible;
            account.Balance = venueAccount.Balance;

            responses.Add(new DiscoveredAccountResponse(
                account.Id,
                account.VenueAccountKey,
                account.Name,
                account.Stage,
                venueAccount.Mode,
                account.CanTrade,
                account.IsVisible,
                account.Balance));
        }

        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(responses);
    }
}
