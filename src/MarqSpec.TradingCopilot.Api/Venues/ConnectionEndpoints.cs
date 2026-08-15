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
        RouteGroupBuilder group = endpoints.MapGroup("/connections").RequireAuthorization().WithTags("Connections");
        group.MapPost("/", CreateConnectionAsync).WithSummary("Create a firm connection (login).");
        group.MapGet("/", ListConnectionsAsync).WithSummary("List the operator's connections.");
        group.MapGet("/{id:guid}/accounts", ListAccountsAsync)
            .WithSummary("List the accounts discovered through a connection.");
        group.MapPost("/{id:guid}/accounts/discover", DiscoverAccountsAsync)
            .WithSummary("Discover accounts through a connection (mode from the firm's declared conventions).");
        group.MapPut("/{id:guid}/credentials", RotateCredentialsAsync)
            .WithSummary("Rotate a connection's stored credentials.");
        group.MapDelete("/{id:guid}", DeactivateConnectionAsync).WithSummary("Deactivate a connection.");
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

    internal static async Task<IResult> ListAccountsAsync(
        Guid id,
        TradingCopilotDbContext database,
        HostTradingEnvironment environment,
        CancellationToken cancellationToken)
    {
        bool connectionExists = await database.Connections.AnyAsync(candidate => candidate.Id == id, cancellationToken);
        if (!connectionExists)
        {
            return Results.NotFound();
        }

        // The PERSISTED roster -- no venue round-trip. Mode is served from the persisted column, which every
        // mode-moving write point recomputes (gh#7) -- the same truth the DB-level R-14 guard enforces.
        // TradeableHere is the R-14 environment verdict for THIS host (gh#9), computed per request.
        List<Account> accounts = await database.Accounts
            .Where(account => account.ConnectionId == id)
            .OrderBy(account => account.VenueAccountKey)
            .ToListAsync(cancellationToken);

        return Results.Ok(accounts.Select(account => AccountResponse.From(account, environment)).ToList());
    }

    internal static async Task<IResult> DiscoverAccountsAsync(
        Guid id,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IProjectXVenueFactory venueFactory,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        HostTradingEnvironment environment,
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

        List<AccountResponse> responses = [];
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

            // Refresh what the venue/resolver report; StageOverride is the OPERATOR's declaration and is
            // deliberately absent here -- rediscovery never touches it (gh#76).
            account.Name = venueAccount.Name;
            account.Stage = venueAccount.Stage;
            account.CanTrade = venueAccount.CanTrade;
            account.IsVisible = venueAccount.IsVisible;
            account.Balance = venueAccount.Balance;
            // The venue's raw routing flag, refreshed like the rest of what the venue reports. NOT the mode --
            // it is an input the recompute below may or may not be permitted to read (gh#780). It is stored
            // because the other two write points have no venue call of their own.
            account.VenueReportsSimulated = venueAccount.VenueReportsSimulated;

            // Write point 1 of 3 for the persisted mode (gh#7): the resolver may have moved the stage.
            // `venueAccount.Mode` is still deliberately NOT copied -- the domain recomputes from conventions
            // rather than trusting a mode the venue reports.
            account.RecomputeMode(conventions);

            responses.Add(AccountResponse.From(account, environment));
        }

        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(responses);
    }

    /// <summary>Rotates which credential key a connection names (gh#210, R-17, ADR-0015).</summary>
    /// <param name="id">The connection.</param>
    /// <param name="request">The new credential key.</param>
    /// <param name="currentUser">The authenticated operator.</param>
    /// <param name="database">The scoped context (R-20 default-deny applies).</param>
    /// <param name="projectXOptions">The credential key this process actually holds.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The updated connection, or a refusal.</returns>
    internal static async Task<IResult> RotateCredentialsAsync(
        Guid id,
        RotateCredentialsRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        IOptions<ProjectXConnectionOptions> projectXOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.CredentialKey))
        {
            return Results.BadRequest(new { error = "A credential key is required." });
        }

        // The R-20 filter does the scoping: another operator's connection simply is not here, so this is 404 and
        // never 403 -- a 403 would confirm the row exists to someone with no right to know.
        Connection? connection = await database.Connections
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (connection is null)
        {
            return Results.NotFound();
        }

        if (!connection.IsActive)
        {
            return Results.Conflict(new
            {
                error = "This connection is deactivated and is no longer a usable path to the venue. "
                    + "Reactivating it is a separate, deliberate action.",
            });
        }

        // Refuse a key this process cannot serve, rather than persisting it. Accepting it would leave the
        // connection pointing at an unusable credential set -- and the failure would surface later, at the venue,
        // on whatever call happened to come first (one credential set per process, ADR-0015).
        string configuredKey = projectXOptions.Value.CredentialKey;
        string rotatedKey = request.CredentialKey.Trim();
        if (!string.Equals(rotatedKey, configuredKey, StringComparison.Ordinal))
        {
            return Results.Conflict(new
            {
                error = $"This process holds credentials for key '{configuredKey}', not '{rotatedKey}'. "
                    + "One ProjectX credential set per process (ADR-0015); reconfigure or run a process for that key.",
            });
        }

        // No secret moves here BY CONSTRUCTION: the column holds a key that NAMES a credential set held in the
        // environment, so rotation repoints a pointer and there is no secret in the request, the row, or a log.
        connection.CredentialKey = rotatedKey;
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ConnectionResponse(connection.Id, connection.FirmId, connection.Platform, connection.CredentialKey));
    }

    /// <summary>Deactivates a connection and cascades to its accounts — a soft delete (gh#210, R-17, R-20).</summary>
    /// <param name="id">The connection.</param>
    /// <param name="currentUser">The authenticated operator.</param>
    /// <param name="database">The scoped context (R-20 default-deny applies).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Ok on deactivation (idempotent), or 404 when it is not the operator's.</returns>
    internal static async Task<IResult> DeactivateConnectionAsync(
        Guid id,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        Connection? connection = await database.Connections
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (connection is null)
        {
            return Results.NotFound();
        }

        // Deactivate, never remove. The journal -- orders, trades, gate decisions -- references these accounts and
        // is the audit trail; deleting the rows would break it in a way nothing notices until an audit needs them.
        connection.IsActive = false;

        List<Account> accounts = await database.Accounts
            .Where(candidate => candidate.ConnectionId == id)
            .ToListAsync(cancellationToken);

        foreach (Account account in accounts)
        {
            account.IsActive = false;
        }

        // Idempotent: a second call re-asserts the same state and still reports success. Deactivating twice is
        // not an error, and making it one would punish a retry after a dropped response.
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { id = connection.Id, isActive = connection.IsActive, deactivatedAccounts = accounts.Count });
    }
}
