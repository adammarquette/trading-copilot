using MarqSpec.TradingCopilot.Api.Ai;
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
        RouteGroupBuilder group = endpoints.MapGroup("/api/news").RequireAuthorization().WithTags("News");
        group.MapGet("/", GetFeedAsync)
            .WithSummary("The operator's personalized news feed, re-ranked by decayed salience.");
        group.MapPut("/feedback", SetFeedbackAsync)
            .WithSummary("Rate a news item — importance (star/mute, a salience weight) or direction (thumbs up/down, salience-inert).");
        group.MapDelete("/feedback", ClearFeedbackAsync)
            .WithSummary("Clear a rating on one axis (importance or direction) — deletes only that axis's row.");
        return endpoints;
    }

    /// <summary>The personalized feed: recent news re-ranked by the operator's decayed salience profile.</summary>
    internal static async Task<IResult> GetFeedAsync(
        int? limit,
        TradingCopilotDbContext database,
        IOptions<SalienceOptions> options,
        SemanticSalienceAxis semanticAxis,
        CancellationToken cancellationToken)
    {
        SalienceOptions config = options.Value;
        int take = Math.Clamp(limit ?? config.DefaultFeedLimit, 1, config.MaxFeedLimit);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SalienceParameters parameters = config.ToParameters();

        // The operator's own feedback (auto-scoped to them by R-20), grouped by the item it rates. An operator may
        // hold TWO rows per item since gh#762 -- one importance (star/mute), one direction (👍/👎) -- so this is a
        // lookup, not a one-row-per-key dictionary (which would throw on the dual-axis item).
        List<SoftSignalFeedback> feedback = await database.SoftSignalFeedbacks.AsNoTracking().ToListAsync(cancellationToken);
        ILookup<string, SoftSignalFeedback> ownFeedback = feedback.ToLookup(f => f.NewsDedupKey, StringComparer.Ordinal);

        // Aggregate the operator's stars/mutes (joined to the dimensions of the items they rated) into a decayed profile.
        SalienceProfile profile = await BuildProfileAsync(database, feedback, now, parameters, cancellationToken);

        // Candidate window: the most recent news, re-ranked by personalized salience. Nothing is hard-filtered here.
        List<NewsRecord> candidates = await database.News.AsNoTracking()
            .OrderByDescending(item => item.PublishedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        // The semantic-embedding axis (gh#853): score THIS window's candidates by embedding nearness to the items the
        // operator STARRED (mutes shape only the categorical profile). Scoring the actual window candidates -- not a
        // global nearest-N -- is what makes a recent near-a-star item reliably surface; it rides on the item, not the
        // profile, and degrades to an empty map (categorical-only) when the provider or the pgvector read is unavailable.
        List<string> starredKeys = [.. feedback.Where(f => f.Kind == SoftSignalKind.Star).Select(f => f.NewsDedupKey)];
        List<string> candidateKeys = [.. candidates.Select(item => item.DedupKey)];
        IReadOnlyDictionary<string, double> semanticAxisMap =
            await semanticAxis.ForCandidatesAsync(candidateKeys, starredKeys, cancellationToken);

        List<NewsFeedItemResponse> items =
        [
            .. candidates
                .Select(item =>
                {
                    NewsDimensions dimensions = DimensionsOf(item) with
                    {
                        SemanticSimilarity = semanticAxisMap.TryGetValue(item.DedupKey, out double similarity)
                            ? similarity
                            : (double?)null,
                    };
                    SalienceScore score = SalienceScorer.Score(profile, dimensions, parameters);
                    double baseRelevance = BaseRelevanceOf(dimensions);

                    // Split the operator's own feedback by axis (gh#762): the importance kind (star/mute) drives the
                    // salience column; the direction kind (👍/👎) is surfaced as a stored, salience-inert sentiment fact.
                    IEnumerable<SoftSignalFeedback> own = ownFeedback[item.DedupKey];
                    // TryAxis, not Axis(): a read must never 500 on a stray/corrupt stored kind the CK does not forbid --
                    // an unmappable row is simply skipped (mirrors SalienceProfile.Build's fail-safe skip).
                    SoftSignalKind? importance = own.FirstOrDefault(f => f.Kind.TryAxis(out SoftSignalAxis a) && a == SoftSignalAxis.Importance)?.Kind;
                    SoftSignalKind? direction = own.FirstOrDefault(f => f.Kind.TryAxis(out SoftSignalAxis a) && a == SoftSignalAxis.Direction)?.Kind;
                    return NewsFeedItemResponse.From(
                        item, dimensions, baseRelevance, score, baseRelevance * score.Multiplier, importance, direction);
                })
                .OrderByDescending(view => view.Salience)
                .ThenByDescending(view => view.PublishedAt),
        ];

        return Results.Ok(new NewsFeedResponse(items));
    }

    /// <summary>
    /// Rates a news item on one axis — importance (star/mute) or direction (👍/👎). An upsert <b>within the request's
    /// axis</b>: re-rating replaces that axis's row and leaves the OTHER axis untouched, so an operator holds at most
    /// one importance and one direction rating per item, independently (gh#762).
    /// </summary>
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

        // Allowlist the kind across BOTH axes (refuses the unset zero and any out-of-range integer): importance
        // (Star/Mute) or direction (ThumbsUp/ThumbsDown).
        if (request.Kind is not (SoftSignalKind.Star or SoftSignalKind.Mute
            or SoftSignalKind.ThumbsUp or SoftSignalKind.ThumbsDown))
        {
            return Results.BadRequest(new { error = "Kind must be Star, Mute, ThumbsUp, or ThumbsDown." });
        }

        SoftSignalAxis axis = request.Kind.Axis();
        string dedupKey = request.DedupKey.Trim();

        // The rated item must exist -- no feedback on a phantom key.
        if (!await database.News.AnyAsync(item => item.DedupKey == dedupKey, cancellationToken))
        {
            return Results.NotFound(new { error = "No news item with that key." });
        }

        // Upsert WITHIN the request's axis: replace this axis's row, leave the other axis alone. The operator holds at
        // most one row per axis on an item, so re-rating (star->mute, or 👍->👎) replaces; setting the other axis adds.
        SoftSignalFeedback? existing = await FindOnAxisAsync(database, dedupKey, axis, cancellationToken);
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
            // Lost the insert race with a concurrent PUT for the same (operator, item, AXIS): that axis's filtered
            // unique index rejected this second insert. Re-read the same-axis row the winner committed and apply this
            // request's kind, so the outcome is the caller's intent rather than a 500. A concurrent PUT on the OTHER
            // axis writes a different row under a different filtered index, so it never collides here.
            database.ChangeTracker.Clear();
            SoftSignalFeedback? winner = await FindOnAxisAsync(database, dedupKey, axis, cancellationToken);
            if (winner is null)
            {
                throw; // not our conflict -- surface it rather than swallow
            }

            ApplyKind(winner, request.Kind);
            await database.SaveChangesAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Clears one axis's rating on an item — importance (star/mute) or direction (👍/👎). Deletes only that axis's
    /// row; the other axis is untouched. The axis defaults to <see cref="SoftSignalAxis.Importance"/> when omitted, so
    /// the pre-gh#762 <c>?dedupKey=</c> contract still clears the star/mute.
    /// </summary>
    internal static async Task<IResult> ClearFeedbackAsync(
        string dedupKey,
        SoftSignalAxis? axis,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dedupKey))
        {
            return Results.BadRequest(new { error = "A news dedup key is required." });
        }

        SoftSignalAxis target = axis ?? SoftSignalAxis.Importance;
        if (target is not (SoftSignalAxis.Importance or SoftSignalAxis.Direction))
        {
            return Results.BadRequest(new { error = "Axis must be Importance or Direction." });
        }

        string key = dedupKey.Trim();
        SoftSignalFeedback? existing = await FindOnAxisAsync(database, key, target, cancellationToken);
        if (existing is null)
        {
            return Results.NotFound();
        }

        database.SoftSignalFeedbacks.Remove(existing);
        await database.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // The operator's row on one axis of one item (the R-20 filter scopes to the operator). At most two rows exist per
    // item -- one per axis -- so this loads them and matches by axis in memory; SoftSignalKind.Axis is the source of
    // truth for the split, and the set is tiny. Returns a TRACKED entity, so the caller can update or remove it.
    private static async Task<SoftSignalFeedback?> FindOnAxisAsync(
        TradingCopilotDbContext database, string dedupKey, SoftSignalAxis axis, CancellationToken cancellationToken)
    {
        List<SoftSignalFeedback> rows = await database.SoftSignalFeedbacks
            .Where(feedback => feedback.NewsDedupKey == dedupKey)
            .ToListAsync(cancellationToken);
        return rows.FirstOrDefault(feedback => feedback.Kind.TryAxis(out SoftSignalAxis rowAxis) && rowAxis == axis);
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
