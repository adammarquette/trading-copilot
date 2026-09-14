using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Signals;

namespace MarqSpec.TradingCopilot.Api.Signals;

/// <summary>A rating request on a news item (gh#27, gh#762). The dedup key rides in the body — it is a URL, not path-safe.</summary>
/// <param name="DedupKey">The rated item's dedup key (its <see cref="NewsRecord"/> primary key).</param>
/// <param name="Kind">
/// The rating — importance (<see cref="SoftSignalKind.Star"/> / <see cref="SoftSignalKind.Mute"/>) or direction
/// (<see cref="SoftSignalKind.ThumbsUp"/> / <see cref="SoftSignalKind.ThumbsDown"/>); never
/// <see cref="SoftSignalKind.Unknown"/>. It is applied within its own axis, independently of the other.
/// </param>
public sealed record NewsFeedbackRequest(string DedupKey, SoftSignalKind Kind);

/// <summary>One explained reason an item was reweighted (gh#27) — so weighting is never a hidden score (ADR-0014).</summary>
/// <param name="Dimension">The shared dimension (<c>Instrument</c> / <c>Topic</c> / <c>Source</c>).</param>
/// <param name="Value">The shared value (for example <c>ES</c>).</param>
/// <param name="Contribution">Its signed contribution to the multiplier (positive from stars, negative from mutes).</param>
public sealed record SalienceReasonResponse(string Dimension, string Value, double Contribution)
{
    /// <summary>Projects a domain reason into its API view.</summary>
    /// <param name="reason">The domain reason.</param>
    /// <returns>The response.</returns>
    public static SalienceReasonResponse From(SalienceReason reason) =>
        new(reason.Dimension.ToString(), reason.Value, Math.Round(reason.Contribution, 4));
}

/// <summary>
/// One ranked item in the operator's personalized news feed (gh#27, gh#762, ADR-0014): the item, its base relevance,
/// the personalized <see cref="Multiplier"/> and resulting <see cref="Salience"/>, the explanation, and the operator's
/// own feedback on it (if any) — on both axes: importance (<see cref="Feedback"/>) and direction
/// (<see cref="Direction"/>).
/// </summary>
public sealed record NewsFeedItemResponse(
    string DedupKey,
    string Title,
    string Url,
    DateTimeOffset PublishedAt,
    IReadOnlyList<string> Instruments,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> Sources,
    double BaseRelevance,
    double Multiplier,
    double Salience,
    string WhyWeighted,
    IReadOnlyList<SalienceReasonResponse> Reasons,
    SoftSignalKind? Feedback,
    SoftSignalKind? Direction,
    string? DirectionReason)
{
    /// <summary>Projects a scored news item into its feed view.</summary>
    /// <param name="item">The news item.</param>
    /// <param name="dimensions">Its similarity dimensions.</param>
    /// <param name="baseRelevance">Its base relevance (pre-personalization).</param>
    /// <param name="score">The personalized salience score.</param>
    /// <param name="salience">The resulting salience (<paramref name="baseRelevance"/> × multiplier).</param>
    /// <param name="feedback">The operator's own <b>importance</b> rating (star/mute) on this item, or <see langword="null"/>.</param>
    /// <param name="direction">The operator's own <b>direction</b> rating (👍/👎) on this item, or <see langword="null"/>. Salience-inert (gh#762).</param>
    /// <returns>The feed item.</returns>
    public static NewsFeedItemResponse From(
        NewsRecord item,
        NewsDimensions dimensions,
        double baseRelevance,
        SalienceScore score,
        double salience,
        SoftSignalKind? feedback,
        SoftSignalKind? direction) =>
        new(
            item.DedupKey,
            item.Title,
            item.Url,
            item.PublishedAt,
            dimensions.Instruments,
            dimensions.Topics,
            dimensions.Sources,
            Math.Round(baseRelevance, 4),
            Math.Round(score.Multiplier, 4),
            Math.Round(salience, 4),
            Explain(score),
            [.. score.Reasons.Select(SalienceReasonResponse.From)],
            feedback,
            direction,
            ExplainDirection(direction));

    // The operator's direction rating (gh#762) is a stored SENTIMENT fact for R-9 learning + display -- it is
    // salience-INERT, so it carries its OWN plain-language reason, never a salience contribution / "why weighted".
    // The wording states that direction does not change what surfaces, keeping the read honest about the two axes.
    // Null when unrated, so a consumer can tell "not rated" from a real direction.
    private static string? ExplainDirection(SoftSignalKind? direction) => direction switch
    {
        SoftSignalKind.ThumbsUp =>
            "You rated this bullish (thumbs up). Direction feeds the learning loop (R-9); it does not change what surfaces.",
        SoftSignalKind.ThumbsDown =>
            "You rated this bearish (thumbs down). Direction feeds the learning loop (R-9); it does not change what surfaces.",
        _ => null,
    };

    // A one-line summary of the reasons; the structured Reasons carry the precise signed contributions behind it.
    private static string Explain(SalienceScore score)
    {
        if (score.Reasons.Count == 0)
        {
            return "Base relevance — no matching feedback yet.";
        }

        double net = score.Reasons.Sum(reason => reason.Contribution);
        string direction = net >= 0 ? "up because you starred" : "down because you muted";
        string shared = string.Join(" and ", score.Reasons.Take(2).Select(Label));
        return $"Weighted {direction} similar items {shared}.";
    }

    private static string Label(SalienceReason reason) => reason.Dimension switch
    {
        SalienceDimension.Instrument => $"on {reason.Value}",
        SalienceDimension.Topic => $"about {reason.Value}",
        SalienceDimension.Source => $"from {reason.Value}",
        // The semantic axis is operator-relative (nearness to the whole starred set), so — unlike the categorical
        // dimensions — it reads no per-value; it explains the "why weighted" as similarity in meaning (gh#853).
        SalienceDimension.SemanticEmbedding => "in meaning",
        _ => reason.Value,
    };
}

/// <summary>The operator's personalized news feed (gh#27), ranked by salience (highest first).</summary>
/// <param name="Items">The ranked items.</param>
public sealed record NewsFeedResponse(IReadOnlyList<NewsFeedItemResponse> Items);
