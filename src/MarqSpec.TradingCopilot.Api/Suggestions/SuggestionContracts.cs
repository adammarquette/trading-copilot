using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Api.Suggestions;

/// <summary>
/// One suggestion as the operator sees it (gh#540, R-4): the persisted spine plus the one figure that is derived
/// rather than stored — <see cref="RewardRiskRatio"/>.
/// </summary>
/// <remarks>
/// <para>
/// The owning <c>UserId</c> is deliberately <b>not</b> projected: it is always the authenticated caller (the R-20
/// filter guarantees it), so echoing it back adds nothing and needlessly surfaces the tenancy key.
/// </para>
/// <para>
/// Fields the epic adds later — rationale, confidence, the validity window, version / supersedes — each become a
/// one-line addition here. That is why this read model lands <b>before</b> those columns (gh#540): a column nothing
/// can observe is the anti-pattern this ordering avoids.
/// </para>
/// </remarks>
/// <param name="Id">The suggestion's id.</param>
/// <param name="AccountId">The account it targets.</param>
/// <param name="Instrument">The venue-neutral instrument symbol.</param>
/// <param name="Side">The proposed direction.</param>
/// <param name="Size">The proposed size in contracts — the operator's trigger's, never the model's.</param>
/// <param name="EntryPrice">The proposed entry.</param>
/// <param name="StopPrice">The proposed protective stop.</param>
/// <param name="TargetPrice">The proposed target.</param>
/// <param name="Mode">The mode it was issued under (R-14).</param>
/// <param name="State">Its lifecycle state.</param>
/// <param name="CreatedAt">When it was issued.</param>
/// <param name="RewardRiskRatio">
/// Reward divided by risk as a unit-free multiple (the wireframe's <c>2.2R</c>), or <see langword="null"/> when risk
/// is zero — a row whose stop equals its entry cannot express a ratio, and the read model must not divide by zero.
/// </param>
public sealed record SuggestionResponse(
    Guid Id,
    Guid AccountId,
    string Instrument,
    OrderSide Side,
    int Size,
    decimal EntryPrice,
    decimal StopPrice,
    decimal TargetPrice,
    TradingMode Mode,
    SuggestionState State,
    DateTimeOffset CreatedAt,
    decimal? RewardRiskRatio)
{
    /// <summary>Projects a persisted suggestion into its API view.</summary>
    /// <param name="suggestion">The persisted row.</param>
    /// <returns>The response.</returns>
    public static SuggestionResponse From(Suggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        return new SuggestionResponse(
            suggestion.Id,
            suggestion.AccountId,
            suggestion.Instrument,
            suggestion.Side,
            suggestion.Size,
            suggestion.EntryPrice,
            suggestion.StopPrice,
            suggestion.TargetPrice,
            suggestion.Mode,
            suggestion.State,
            suggestion.CreatedAt,
            RatioOf(suggestion.EntryPrice, suggestion.StopPrice, suggestion.TargetPrice));
    }

    // Magnitudes, so the arithmetic is identical for a long and a short -- the geometry is inverted between them but
    // the ratio is not. Geometry is validated at issuance (SuggestionGeometry), but this projects rows it did not
    // write, so a zero-risk row yields null rather than throwing or reporting infinity.
    private static decimal? RatioOf(decimal entry, decimal stop, decimal target)
    {
        decimal risk = Math.Abs(entry - stop);
        return risk == 0m ? null : Math.Abs(target - entry) / risk;
    }
}

/// <summary>A page of suggestions (gh#540).</summary>
/// <param name="Items">The suggestions, newest first.</param>
public sealed record SuggestionListResponse(IReadOnlyList<SuggestionResponse> Items);
