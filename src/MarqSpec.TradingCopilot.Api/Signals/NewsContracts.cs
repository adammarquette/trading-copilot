using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Signals;

namespace MarqSpec.TradingCopilot.Api.Signals;

/// <summary>A star/mute request on a news item (gh#27). The dedup key rides in the body — it is a URL, not path-safe.</summary>
/// <param name="DedupKey">The rated item's dedup key (its <see cref="NewsRecord"/> primary key).</param>
/// <param name="Kind">Star or mute (never <see cref="SoftSignalKind.Unknown"/>).</param>
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
/// One ranked item in the operator's personalized news feed (gh#27, ADR-0014): the item, its base relevance, the
/// personalized <see cref="Multiplier"/> and resulting <see cref="Salience"/>, the explanation, and the operator's
/// own feedback on it (if any).
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
    SoftSignalKind? Feedback)
{
    /// <summary>Projects a scored news item into its feed view.</summary>
    /// <param name="item">The news item.</param>
    /// <param name="dimensions">Its similarity dimensions.</param>
    /// <param name="baseRelevance">Its base relevance (pre-personalization).</param>
    /// <param name="score">The personalized salience score.</param>
    /// <param name="salience">The resulting salience (<paramref name="baseRelevance"/> × multiplier).</param>
    /// <param name="feedback">The operator's own star/mute on this item, or <see langword="null"/>.</param>
    /// <returns>The feed item.</returns>
    public static NewsFeedItemResponse From(
        NewsRecord item,
        NewsDimensions dimensions,
        double baseRelevance,
        SalienceScore score,
        double salience,
        SoftSignalKind? feedback) =>
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
            feedback);

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
