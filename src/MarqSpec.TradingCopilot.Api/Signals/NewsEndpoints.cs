using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Signals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Signals;

/// <summary>
/// The <c>/api/news</c> endpoints (gh#27, ADR-0014): the operator's <b>personalized</b> news feed and the star/mute
/// feedback that shapes it. A star raises the salience of similar future items; a mute lowers it (never hiding it).
/// </summary>
/// <remarks>
/// This is a <b>soft salience weight only</b>: a star reorders what the operator sees, and cannot move a risk limit,
/// a position size, or a gate decision — the read here is the sole consumer of <see cref="SoftSignalFeedback"/>, and
/// no risk / execution path references it (ADR-0007, enforcement below the model). Every read and write is R-20-scoped
/// to the operator by the DbContext filter; feedback rows additionally carry the owning user set on write.
/// </remarks>
public static class NewsEndpoints
{
    /// <summary>Maps the news feed + feedback endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapNewsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/news").RequireAuthorization();
        group.MapGet("/", GetFeedAsync);
        group.MapPut("/feedback", SetFeedbackAsync);
        group.MapDelete("/feedback", ClearFeedbackAsync);
        return endpoints;
    }

    /// <summary>The personalized feed: recent news re-ranked by the operator's decayed salience profile.</summary>
    internal static async Task<IResult> GetFeedAsync(
        int? limit,
        TradingCopilotDbContext database,
        IOptions<SalienceOptions> options,
        CancellationToken cancellationToken)
    {
        SalienceOptions config = options.Value;
        int take = Math.Clamp(limit ?? config.DefaultFeedLimit, 1, config.MaxFeedLimit);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SalienceParameters parameters = config.ToParameters();

        // The operator's own feedback (auto-scoped to them by R-20), keyed by the item it rates.
        List<SoftSignalFeedback> feedback = await database.SoftSignalFeedbacks.AsNoTracking().ToListAsync(cancellationToken);
        Dictionary<string, SoftSignalFeedback> ownFeedback = feedback.ToDictionary(f => f.NewsDedupKey, StringComparer.Ordinal);

        // Aggregate the operator's stars/mutes (joined to the dimensions of the items they rated) into a decayed profile.
        SalienceProfile profile = await BuildProfileAsync(database, feedback, now, parameters, cancellationToken);

        // Candidate window: the most recent news, re-ranked by personalized salience. Nothing is hard-filtered here.
        List<NewsRecord> candidates = await database.News.AsNoTracking()
            .OrderByDescending(item => item.PublishedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        List<NewsFeedItemResponse> items =
        [
            .. candidates
                .Select(item =>
                {
                    NewsDimensions dimensions = DimensionsOf(item);
                    SalienceScore score = SalienceScorer.Score(profile, dimensions, parameters);
                    double baseRelevance = BaseRelevanceOf(dimensions);
                    ownFeedback.TryGetValue(item.DedupKey, out SoftSignalFeedback? own);
                    return NewsFeedItemResponse.From(
                        item, dimensions, baseRelevance, score, baseRelevance * score.Multiplier, own?.Kind);
                })
                .OrderByDescending(view => view.Salience)
                .ThenByDescending(view => view.PublishedAt),
        ];

        return Results.Ok(new NewsFeedResponse(items));
    }

    /// <summary>Stars or mutes a news item — an upsert: re-rating replaces, so an item carries at most one feedback.</summary>
    internal static async Task<IResult> SetFeedbackAsync(
        NewsFeedbackRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DedupKey))
        {
            return Results.BadRequest(new { error = "A news dedup key is required." });
        }

        // Allowlist the kind: star or mute (refuses the unset zero and any out-of-range integer).
        if (request.Kind is not (SoftSignalKind.Star or SoftSignalKind.Mute))
        {
            return Results.BadRequest(new { error = "Kind must be Star or Mute." });
        }

        string dedupKey = request.DedupKey.Trim();

        // The rated item must exist -- no feedback on a phantom key.
        if (!await database.News.AnyAsync(item => item.DedupKey == dedupKey, cancellationToken))
        {
            return Results.NotFound(new { error = "No news item with that key." });
        }

        // Upsert on the unique (UserId, NewsDedupKey): re-rating replaces the kind (un-starring is a separate DELETE).
        SoftSignalFeedback? existing = await database.SoftSignalFeedbacks
            .FirstOrDefaultAsync(f => f.NewsDedupKey == dedupKey, cancellationToken);
        if (existing is not null)
        {
            ApplyKind(existing, request.Kind);
            await database.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        database.SoftSignalFeedbacks.Add(new SoftSignalFeedback
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId,
            NewsDedupKey = dedupKey,
            Kind = request.Kind,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost the insert race with a concurrent PUT for the same (operator, item): the unique
            // (UserId, NewsDedupKey) index rejected this second insert. Re-read the row the winner committed and apply
            // this request's kind, so the outcome is the caller's intent (a star ends up starred) rather than a 500.
            database.ChangeTracker.Clear();
            SoftSignalFeedback? winner = await database.SoftSignalFeedbacks
                .FirstOrDefaultAsync(f => f.NewsDedupKey == dedupKey, cancellationToken);
            if (winner is null)
            {
                throw; // not our conflict -- surface it rather than swallow
            }

            ApplyKind(winner, request.Kind);
            await database.SaveChangesAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    /// <summary>Un-stars / un-mutes an item — the personalization round-trips, so its salience returns to base.</summary>
    internal static async Task<IResult> ClearFeedbackAsync(
        string dedupKey,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dedupKey))
        {
            return Results.BadRequest(new { error = "A news dedup key is required." });
        }

        string key = dedupKey.Trim();
        SoftSignalFeedback? existing = await database.SoftSignalFeedbacks
            .FirstOrDefaultAsync(f => f.NewsDedupKey == key, cancellationToken);
        if (existing is null)
        {
            return Results.NotFound();
        }

        database.SoftSignalFeedbacks.Remove(existing);
        await database.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // Apply a re-rating: a genuine kind change resets the recency clock; re-applying the SAME kind is a true no-op, so
    // an idempotent PUT does not silently move the item's salience just by refreshing its timestamp.
    private static void ApplyKind(SoftSignalFeedback feedback, SoftSignalKind kind)
    {
        if (feedback.Kind == kind)
        {
            return;
        }

        feedback.Kind = kind;
        feedback.CreatedAt = DateTimeOffset.UtcNow;
    }

    // Aggregate one operator's feedback into a decayed profile: load the dimensions of the items they rated (news is
    // global, so it is not scoped) and hand them to the pure builder. A rated item that no longer exists simply
    // contributes nothing.
    private static async Task<SalienceProfile> BuildProfileAsync(
        TradingCopilotDbContext database,
        List<SoftSignalFeedback> feedback,
        DateTimeOffset now,
        SalienceParameters parameters,
        CancellationToken cancellationToken)
    {
        if (feedback.Count == 0)
        {
            return SalienceProfile.Build([], now, parameters);
        }

        List<string> ratedKeys = [.. feedback.Select(f => f.NewsDedupKey)];
        Dictionary<string, NewsRecord> ratedItems = await database.News.AsNoTracking()
            .Where(item => ratedKeys.Contains(item.DedupKey))
            .ToDictionaryAsync(item => item.DedupKey, StringComparer.Ordinal, cancellationToken);

        List<SoftSignalRating> ratings = [];
        foreach (SoftSignalFeedback rating in feedback)
        {
            if (ratedItems.TryGetValue(rating.NewsDedupKey, out NewsRecord? item))
            {
                ratings.Add(new SoftSignalRating(rating.Kind, DimensionsOf(item), rating.CreatedAt));
            }
        }

        return SalienceProfile.Build(ratings, now, parameters);
    }

    private static NewsDimensions DimensionsOf(NewsRecord item) =>
        new(item.MatchedInstruments, item.MatchedTopics, item.SourceFeeds);

    // Base relevance from the R-2 signals (gh#359): a matched item outranks an unmatched one even at cold-start, and
    // nothing is hard-filtered -- an unmatched item still surfaces, at a lower base (the ADR-0014 "no filter bubble"
    // property). v1 is deliberately coarse; a richer base (match count / recency) is an ADR-0014 follow-up.
    private static double BaseRelevanceOf(NewsDimensions dimensions) =>
        dimensions.Instruments.Count + dimensions.Topics.Count > 0 ? 1.0 : 0.5;
}
