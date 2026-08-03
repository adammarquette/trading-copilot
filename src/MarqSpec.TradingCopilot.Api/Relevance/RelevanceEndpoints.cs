using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Relevance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Relevance;

/// <summary>
/// The <c>/api/relevance</c> endpoints (gh#359, R-2) — the operator curates the deployment's <b>global</b> relevance
/// config: ticker↔instrument maps and topics. Every mutation bumps the single-row <see cref="RelevanceConfigState"/>
/// so the resolution pass re-resolves the affected news. Global config (a market fact, not per-operator), so not
/// owner-scoped; auth is still required.
/// </summary>
public static class RelevanceEndpoints
{
    private const int MaxSymbolLength = 32;
    private const int MaxNameLength = 64;

    /// <summary>Maps the relevance-config endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapRelevanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/relevance").RequireAuthorization().WithTags("Relevance");
        group.MapPost("/maps", CreateMapAsync).WithSummary("Create a ticker↔instrument relevance map.");
        group.MapGet("/maps", ListMapsAsync).WithSummary("List the ticker↔instrument maps.");
        group.MapDelete("/maps/{ticker}/{instrument}", DeleteMapAsync)
            .WithSummary("Delete a ticker↔instrument map.");
        group.MapPost("/topics", CreateTopicAsync).WithSummary("Create a relevance topic.");
        group.MapGet("/topics", ListTopicsAsync).WithSummary("List relevance topics.");
        group.MapGet("/topics/{id:guid}", GetTopicAsync).WithSummary("Get a relevance topic by id.");
        group.MapPatch("/topics/{id:guid}", UpdateTopicAsync).WithSummary("Update a relevance topic.");
        group.MapDelete("/topics/{id:guid}", DeleteTopicAsync).WithSummary("Delete a relevance topic.");
        return endpoints;
    }

    internal static async Task<IResult> CreateMapAsync(
        TickerMapRequest request, TradingCopilotDbContext database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Ticker) || string.IsNullOrWhiteSpace(request.Instrument))
        {
            return Results.BadRequest(new { error = "Ticker and instrument are required." });
        }

        string ticker = request.Ticker.Trim().ToUpperInvariant();
        string instrument = request.Instrument.Trim().ToUpperInvariant();
        if (ticker.Length > MaxSymbolLength || instrument.Length > MaxSymbolLength)
        {
            return Results.BadRequest(new { error = $"Ticker and instrument must be at most {MaxSymbolLength} characters." });
        }

        if (await database.TickerInstrumentMaps.AnyAsync(m => m.Ticker == ticker && m.Instrument == instrument, cancellationToken))
        {
            return Results.Conflict(new { error = "That mapping already exists." });
        }

        database.TickerInstrumentMaps.Add(new TickerInstrumentMap { Ticker = ticker, Instrument = instrument });
        await TouchConfigAsync(database, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/relevance/maps/{ticker}/{instrument}", new TickerMapResponse(ticker, instrument));
    }

    internal static async Task<IResult> ListMapsAsync(TradingCopilotDbContext database, CancellationToken cancellationToken)
    {
        List<TickerInstrumentMap> maps = await database.TickerInstrumentMaps
            .OrderBy(m => m.Ticker).ThenBy(m => m.Instrument)
            .ToListAsync(cancellationToken);

        return Results.Ok(maps.Select(m => new TickerMapResponse(m.Ticker, m.Instrument)).ToList());
    }

    internal static async Task<IResult> DeleteMapAsync(
        string ticker, string instrument, TradingCopilotDbContext database, CancellationToken cancellationToken)
    {
        string t = ticker.Trim().ToUpperInvariant();
        string i = instrument.Trim().ToUpperInvariant();
        TickerInstrumentMap? map = await database.TickerInstrumentMaps
            .FirstOrDefaultAsync(m => m.Ticker == t && m.Instrument == i, cancellationToken);
        if (map is null)
        {
            return Results.NotFound();
        }

        database.TickerInstrumentMaps.Remove(map);
        await TouchConfigAsync(database, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }

    internal static async Task<IResult> CreateTopicAsync(
        TopicRequest request, TradingCopilotDbContext database, CancellationToken cancellationToken)
    {
        (IResult? error, string name, string? instrument) = ValidateTopic(request);
        if (error is not null)
        {
            return error;
        }

        if (await database.NewsTopics.AnyAsync(t => t.Name == name, cancellationToken))
        {
            return Results.Conflict(new { error = "A topic with that name already exists." });
        }

        NewsTopic topic = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Keywords = CleanKeywords(request.Keywords),
            Scope = request.Scope,
            Instrument = instrument,
        };
        database.NewsTopics.Add(topic);
        await TouchConfigAsync(database, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/relevance/topics/{topic.Id}", TopicResponse.From(topic));
    }

    internal static async Task<IResult> ListTopicsAsync(TradingCopilotDbContext database, CancellationToken cancellationToken)
    {
        List<NewsTopic> topics = await database.NewsTopics.OrderBy(t => t.Name).ToListAsync(cancellationToken);
        return Results.Ok(topics.Select(TopicResponse.From).ToList());
    }

    internal static async Task<IResult> GetTopicAsync(
        Guid id, TradingCopilotDbContext database, CancellationToken cancellationToken)
    {
        NewsTopic? topic = await database.NewsTopics.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        return topic is null ? Results.NotFound() : Results.Ok(TopicResponse.From(topic));
    }

    internal static async Task<IResult> UpdateTopicAsync(
        Guid id, TopicRequest request, TradingCopilotDbContext database, CancellationToken cancellationToken)
    {
        (IResult? error, string name, string? instrument) = ValidateTopic(request);
        if (error is not null)
        {
            return error;
        }

        NewsTopic? topic = await database.NewsTopics.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (topic is null)
        {
            return Results.NotFound();
        }

        if (await database.NewsTopics.AnyAsync(t => t.Name == name && t.Id != id, cancellationToken))
        {
            return Results.Conflict(new { error = "A topic with that name already exists." });
        }

        topic.Name = name;
        topic.Keywords = CleanKeywords(request.Keywords);
        topic.Scope = request.Scope;
        topic.Instrument = instrument;
        await TouchConfigAsync(database, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(TopicResponse.From(topic));
    }

    internal static async Task<IResult> DeleteTopicAsync(
        Guid id, TradingCopilotDbContext database, CancellationToken cancellationToken)
    {
        NewsTopic? topic = await database.NewsTopics.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (topic is null)
        {
            return Results.NotFound();
        }

        database.NewsTopics.Remove(topic);
        await TouchConfigAsync(database, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }

    private static (IResult? Error, string Name, string? Instrument) ValidateTopic(TopicRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (Results.BadRequest(new { error = "A topic name is required." }), string.Empty, null);
        }

        string name = request.Name.Trim();
        if (name.Length > MaxNameLength)
        {
            return (Results.BadRequest(new { error = $"Topic name must be at most {MaxNameLength} characters." }), name, null);
        }

        if (request.Keywords is null || request.Keywords.Count == 0 || request.Keywords.All(string.IsNullOrWhiteSpace))
        {
            return (Results.BadRequest(new { error = "At least one keyword is required." }), name, null);
        }

        // Allowlist the scope: Instrument or Global (refuses the unset zero and any out-of-range integer).
        if (request.Scope is not (TopicScope.Instrument or TopicScope.Global))
        {
            return (Results.BadRequest(new { error = "Scope must be Instrument or Global." }), name, null);
        }

        // An instrument-scoped topic needs its instrument; a global one has none.
        if (request.Scope == TopicScope.Instrument)
        {
            if (string.IsNullOrWhiteSpace(request.Instrument))
            {
                return (Results.BadRequest(new { error = "An instrument-scoped topic requires an instrument." }), name, null);
            }

            string instrument = request.Instrument.Trim().ToUpperInvariant();
            if (instrument.Length > MaxSymbolLength)
            {
                return (Results.BadRequest(new { error = $"Instrument must be at most {MaxSymbolLength} characters." }), name, null);
            }

            return (null, name, instrument);
        }

        return (null, name, null);
    }

    private static List<string> CleanKeywords(IReadOnlyList<string> keywords) =>
        [.. keywords.Select(keyword => keyword.Trim()).Where(keyword => keyword.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    // Stamp the config-version marker so the resolution pass re-resolves the affected news. Not saved here -- the
    // caller commits it in the same transaction as the mutation, so the version and the change land atomically.
    private static async Task TouchConfigAsync(TradingCopilotDbContext database, CancellationToken cancellationToken)
    {
        // Upsert the ONE config-state row by its fixed id, so the table can never fork into two rows.
        RelevanceConfigState? state = await database.RelevanceConfigStates
            .FirstOrDefaultAsync(s => s.Id == RelevanceConfigState.SingletonId, cancellationToken);
        if (state is null)
        {
            // First-ever edit: generation 1. Pre-existing news carries a null RelevanceVersion, so it re-resolves
            // against this on the next pass regardless.
            database.RelevanceConfigStates.Add(
                new RelevanceConfigState { Id = RelevanceConfigState.SingletonId, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 });
        }
        else
        {
            // Increment the monotonic generation (gh#418) — this, not the timestamp, is what the pass compares.
            state.Version++;
            state.UpdatedAt = DateTimeOffset.UtcNow; // observability only
        }
    }
}
