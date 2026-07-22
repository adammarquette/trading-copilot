using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Firms;

/// <summary>
/// The <c>/firms</c> endpoints — where the operator registers the firms they trade with and declares what each
/// account stage means at each (gh#60, gh#76). Every query is default-deny scoped to the operator (R-20), so a
/// firm belongs to exactly one operator's workspace.
/// </summary>
public static class FirmEndpoints
{
    /// <summary>Maps the firm-configuration endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapFirmEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/firms").RequireAuthorization();
        group.MapPost("/", CreateFirmAsync);
        group.MapGet("/", ListFirmsAsync);
        group.MapPut("/{id:guid}/conventions", DeclareConventionsAsync);
        return endpoints;
    }

    internal static async Task<IResult> CreateFirmAsync(
        CreateFirmRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = "A firm name is required." });
        }

        // Scoped to the operator, so the uniqueness is within their workspace -- two operators may both trade
        // "Topstep". The DB unique index is the backstop; this gives a clean 409 instead of a write error.
        bool exists = await database.Firms.AnyAsync(firm => firm.Name == request.Name, cancellationToken);
        if (exists)
        {
            return Results.Conflict(new { error = $"A firm named '{request.Name}' already exists." });
        }

        Firm created = new()
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId,
            Name = request.Name,
            Type = request.Type,
        };

        database.Firms.Add(created);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(FirmResponse.From(created));
    }

    internal static async Task<IResult> ListFirmsAsync(
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        List<Firm> firms = await database.Firms
            .Include(firm => firm.StageConventions)
            .ToListAsync(cancellationToken);

        return Results.Ok(firms.Select(FirmResponse.From).ToList());
    }

    internal static async Task<IResult> DeclareConventionsAsync(
        Guid id,
        DeclareConventionsRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        Firm? firm = await database.Firms
            .Include(candidate => candidate.StageConventions)
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (firm is null)
        {
            return Results.NotFound();
        }

        // Validate through the domain rule that owns "what is a valid set of conventions" -- it rejects an
        // Unknown stage, an undefined stage, and a duplicate stage. Reusing it keeps one source of truth and
        // turns a bad request into a 400 rather than a DB constraint error later.
        FirmConventions conventions;
        try
        {
            conventions = FirmConventions.For(
                firm.Name,
                [.. request.Conventions.Select(convention => (convention.Stage, convention.CapitalAtRisk))]);
        }
        catch (ArgumentException error)
        {
            return Results.BadRequest(new { error = error.Message });
        }

        // Replace the whole set: the request is the firm's complete declared conventions. Remove the old and
        // add the new through the DbSet -- reassigning the tracked navigation reference makes EF mis-read the
        // pre-keyed children as updates rather than inserts.
        database.FirmStageConventions.RemoveRange(firm.StageConventions);

        List<FirmStageConvention> declared =
        [
            .. request.Conventions.Select(convention => new FirmStageConvention
            {
                Id = Guid.NewGuid(),
                UserId = currentUser.UserId,
                FirmId = firm.Id,
                Stage = convention.Stage,
                CapitalAtRisk = convention.CapitalAtRisk,
            }),
        ];
        database.FirmStageConventions.AddRange(declared);

        // Write point 3 of 3 for the persisted mode (gh#7): a re-declaration changes what every stage MEANS,
        // so every account under this firm is swept -- otherwise the DB-level R-14 guard would enforce
        // yesterday's declaration.
        List<Data.Entities.Account> affected = await database.Accounts
            .Join(
                database.Connections.Where(connection => connection.FirmId == firm.Id),
                account => account.ConnectionId,
                connection => connection.Id,
                (account, _) => account)
            .ToListAsync(cancellationToken);
        foreach (Data.Entities.Account account in affected)
        {
            account.RecomputeMode(conventions);
        }

        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(new FirmResponse(
            firm.Id,
            firm.Name,
            firm.Type,
            [.. declared.Select(convention => new StageConventionDto(convention.Stage, convention.CapitalAtRisk))]));
    }
}
