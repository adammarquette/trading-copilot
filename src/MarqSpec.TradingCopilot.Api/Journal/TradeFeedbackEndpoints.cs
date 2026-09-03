using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Journal;
using MarqSpec.TradingCopilot.Domain.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Journal;

/// <summary>
/// The post-close trade feedback read + write surface (gh#1064, R-8): <c>GET /trades/{tradeId}/feedback</c> reads a
/// trade's feedback entries and whether it is <b>awaiting review</b>, and <c>POST /trades/{tradeId}/feedback</c>
/// lets the operator attach an entry — optionally and asynchronously, "anytime" after the trade closes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never on the close path.</b> This surface only ever <i>reads</i> <see cref="Data.Entities.Trade"/> — it adds
/// no column and no write to that table or its writer, so a feedback write can never delay or fail a trade close
/// (the gh#289 lesson: a journal write on a hot path is a defect). A brand-new, self-contained route group and
/// reader (<see cref="TradeFeedbackReader"/>) rather than an addition to <c>DailyJournalEndpoints</c> /
/// <c>DailyRealizedReader</c> (gh#1062) — those stay untouched, since gh#1087 is landing alongside this in the same
/// directory.
/// </para>
/// <para>
/// <b>"Awaiting review" is derived, not stored</b> (<see cref="TradeReviewPolicy"/>): true once a trade has closed
/// and carries no <see cref="FeedbackAuthor.Operator"/>-authored entry, false the moment one exists, and never true
/// before the trade closes. Feedback is <b>never required</b> — nothing here blocks or expires a trade for lacking it.
/// </para>
/// <para>
/// <b>Tenancy is the DbContext's.</b> Every handler is an ordinary request path, so the automatic <c>IUserOwned</c>
/// default-deny filter (R-20 / ADR-0017) applies to both <see cref="Data.Entities.Trade"/> and
/// <see cref="TradeFeedback"/> — a stranger's trade is a <b>404</b>, never disclosed, and its feedback is invisible
/// (and, on a foreign trade, never written).
/// </para>
/// </remarks>
public static class TradeFeedbackEndpoints
{
    /// <summary>Maps the trade-feedback endpoints under <c>/trades/{tradeId}/feedback</c>. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapTradeFeedbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup("/trades/{tradeId:guid}/feedback")
            .RequireAuthorization()
            .WithTags("Journal");

        group.MapGet("/", GetFeedbackAsync)
            .WithSummary("Reads a trade's feedback entries and whether it is awaiting the operator's review (R-8).");

        // now is injected (not bound) so CreatedAt is testable, mirroring the outcome hard-delete audit timestamp.
        group.MapPost("/", (Guid tradeId, AddTradeFeedbackRequest? request, TradingCopilotDbContext database, CancellationToken cancellationToken) =>
                AddFeedbackAsync(tradeId, request, DateTimeOffset.UtcNow, database, cancellationToken))
            .WithSummary("Attaches operator feedback to a closed trade (R-8) -- optional, asynchronous, never on the close path.");

        return endpoints;
    }

    /// <summary>
    /// Reads a trade's feedback (<c>GET /trades/{tradeId}/feedback</c>, R-8), oldest first, with the derived
    /// awaiting-review flag.
    /// </summary>
    /// <param name="tradeId">The trade to read.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The summary (200), or a 404 for an absent / foreign trade (R-20).</returns>
    internal static async Task<IResult> GetFeedbackAsync(
        Guid tradeId,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        Trade? trade = await database.Trades.FirstOrDefaultAsync(candidate => candidate.Id == tradeId, cancellationToken);
        if (trade is null)
        {
            return Results.NotFound(); // absent or foreign (R-20) -- never a disclosure
        }

        IReadOnlyList<TradeFeedback> entries = await database.FeedbackForTradeAsync(tradeId, cancellationToken);
        bool awaitingReview = TradeReviewPolicy.IsAwaitingReview(
            trade.ClosedAt is not null, entries.Select(entry => entry.Author));

        return Results.Ok(new TradeFeedbackSummaryResponse(
            tradeId, awaitingReview, [.. entries.Select(TradeFeedbackEntryResponse.From)]));
    }

    /// <summary>
    /// Attaches operator feedback to a closed trade (<c>POST /trades/{tradeId}/feedback</c>, R-8) — optional,
    /// asynchronous, and never required to close or record a trade.
    /// </summary>
    /// <param name="tradeId">The trade to annotate.</param>
    /// <param name="request">The feedback fields; at least one must carry content.</param>
    /// <param name="now">The clock, injected so <see cref="TradeFeedback.CreatedAt"/> is testable.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// 201 with the created entry; 404 for an absent / foreign trade (R-20); 400 when the trade has not closed yet,
    /// when nothing was submitted, or when a field exceeds its max length.
    /// </returns>
    internal static async Task<IResult> AddFeedbackAsync(
        Guid tradeId,
        AddTradeFeedbackRequest? request,
        DateTimeOffset now,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        Trade? trade = await database.Trades.FirstOrDefaultAsync(candidate => candidate.Id == tradeId, cancellationToken);
        if (trade is null)
        {
            return Results.NotFound(); // absent or foreign (R-20) -- never a disclosure, and nothing is written
        }

        if (trade.ClosedAt is null)
        {
            return Results.BadRequest(new { error = "Feedback can only be attached to a closed trade." });
        }

        string? comment = Normalize(request?.Comment);
        if (comment is not null && comment.Length > TradeFeedback.CommentMaxLength)
        {
            return Results.BadRequest(new { error = $"Comment must be at most {TradeFeedback.CommentMaxLength} characters." });
        }

        string? emotionalState = Normalize(request?.EmotionalState);
        if (emotionalState is not null && emotionalState.Length > TradeFeedback.EmotionalStateMaxLength)
        {
            return Results.BadRequest(new { error = $"EmotionalState must be at most {TradeFeedback.EmotionalStateMaxLength} characters." });
        }

        List<string> tags = [.. (request?.Tags ?? [])
            .Select(Normalize)
            .OfType<string>()
            .Distinct()];

        if (comment is null && emotionalState is null && tags.Count == 0)
        {
            return Results.BadRequest(new { error = "Feedback must include a comment, a tag, or an emotional state." });
        }

        TradeFeedback feedback = new()
        {
            Id = Guid.NewGuid(),
            UserId = trade.UserId, // the trade's own owner -- database.Trades already proved it is the caller's (R-20)
            TradeId = tradeId,
            Comment = comment,
            Tags = tags,
            EmotionalState = emotionalState,
            Author = FeedbackAuthor.Operator, // this endpoint is the operator's own path; no AI writer exists yet
            CreatedAt = now,
        };

        database.TradeFeedbacks.Add(feedback);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Created($"/trades/{tradeId}/feedback/{feedback.Id}", TradeFeedbackEntryResponse.From(feedback));
    }

    /// <summary>Trims a submitted field and turns blank input into <see langword="null"/> (never stores whitespace).</summary>
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A request to attach feedback to a closed trade (gh#1064, R-8). At least one of the three must carry content —
/// an entirely empty submission is refused (400), the same posture the DB's own <c>CK_TradeFeedback_HasContent</c>
/// backs up.
/// </summary>
/// <param name="Comment">An optional free-text note, capped at <see cref="TradeFeedback.CommentMaxLength"/>.</param>
/// <param name="Tags">Optional free-form labels; blank entries are dropped and duplicates collapse.</param>
/// <param name="EmotionalState">An optional short label, capped at <see cref="TradeFeedback.EmotionalStateMaxLength"/>.</param>
public sealed record AddTradeFeedbackRequest(string? Comment, IReadOnlyList<string>? Tags, string? EmotionalState);

/// <summary>One feedback entry for the read surface (gh#1064, R-8).</summary>
/// <param name="Id">The entry's id.</param>
/// <param name="Comment">The free-text note, when one was given.</param>
/// <param name="Tags">The labels attached, if any.</param>
/// <param name="EmotionalState">The state-of-mind label, when one was given.</param>
/// <param name="Author">Who wrote it, as a name (<c>Operator</c> / <c>Ai</c>).</param>
/// <param name="CreatedAt">When it was recorded.</param>
public sealed record TradeFeedbackEntryResponse(
    Guid Id, string? Comment, IReadOnlyList<string> Tags, string? EmotionalState, string Author, DateTimeOffset CreatedAt)
{
    /// <summary>Projects a <see cref="TradeFeedback"/> to its response.</summary>
    /// <param name="feedback">The feedback row to project.</param>
    /// <returns>The response.</returns>
    public static TradeFeedbackEntryResponse From(TradeFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        return new TradeFeedbackEntryResponse(
            feedback.Id, feedback.Comment, feedback.Tags, feedback.EmotionalState, feedback.Author.ToString(), feedback.CreatedAt);
    }
}

/// <summary>A trade's feedback read (gh#1064, R-8) — the entries plus the derived awaiting-review flag.</summary>
/// <param name="TradeId">The trade read.</param>
/// <param name="AwaitingReview">
/// <see langword="true"/> when the trade has closed and no operator-authored feedback exists for it yet;
/// <see langword="false"/> once it has, and always <see langword="false"/> before the trade closes.
/// </param>
/// <param name="Entries">The trade's feedback, oldest first.</param>
public sealed record TradeFeedbackSummaryResponse(Guid TradeId, bool AwaitingReview, IReadOnlyList<TradeFeedbackEntryResponse> Entries);
