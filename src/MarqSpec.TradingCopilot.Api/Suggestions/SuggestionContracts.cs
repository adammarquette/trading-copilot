using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Suggestions;
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
/// <param name="TimeframeMinutes">
/// The suggestion's <b>headline timeframe</b> (gh#592, R-4) — the bar size it is framed on, so the card can tell a
/// scalp from a swing. In today's single-signal model it is the cited indicator's resolution (see
/// <paramref name="CitedResolutionMinutes"/>), surfaced here as a first-class attribute rather than provenance.
/// </param>
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
/// <param name="RiskUsd">
/// What the whole position loses at its stop, in dollars (gh#541) — <see langword="null"/> when the instrument has no
/// configured contract spec, because a guessed tick size is worse than an absent figure.
/// </param>
/// <param name="RewardUsd">What the whole position makes at its target, in dollars; <see langword="null"/> likewise.</param>
/// <param name="Rationale">
/// The model's plain-language reasoning (gh#542). <b>Untrusted display data</b> — render it as text, never as markup,
/// and never feed it back into a prompt as instruction.
/// </param>
/// <param name="CitedIndicator">The R-22 indicator that fired, copied at issuance so the citation survives the trigger (gh#542).</param>
/// <param name="CitedPeriod">The fired indicator's period (gh#542).</param>
/// <param name="CitedResolutionMinutes">The bar size the fired indicator was computed over (gh#542).</param>
/// <param name="Confidence">The model's confidence, 0–100 (gh#543) — <b>display only</b>; it moves nothing.</param>
/// <param name="ExpiresAt">
/// When the suggestion stops being actionable (gh#544) — the system's value, clamped to the session's auto-flatten
/// deadline. The client derives the countdown from this and its own clock rather than a server-computed remainder,
/// which would be stale the instant it was serialized.
/// </param>
public sealed record SuggestionResponse(
    Guid Id,
    Guid AccountId,
    string Instrument,
    int TimeframeMinutes,
    OrderSide Side,
    int Size,
    decimal EntryPrice,
    decimal StopPrice,
    decimal TargetPrice,
    TradingMode Mode,
    SuggestionState State,
    DateTimeOffset CreatedAt,
    decimal? RewardRiskRatio,
    decimal? RiskUsd,
    decimal? RewardUsd,
    string Rationale,
    string CitedIndicator,
    int CitedPeriod,
    int CitedResolutionMinutes,
    int Confidence,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Projects a persisted suggestion into its API view.</summary>
    /// <param name="suggestion">The persisted row.</param>
    /// <param name="spec">
    /// The instrument's contract facts (gh#541) used to money-value the geometry, or <see langword="null"/> when the
    /// instrument is not configured — in which case the dollar figures are omitted rather than guessed.
    /// </param>
    /// <returns>The response.</returns>
    public static SuggestionResponse From(Suggestion suggestion, InstrumentContractSpec? spec = null)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        return new SuggestionResponse(
            suggestion.Id,
            suggestion.AccountId,
            suggestion.Instrument,
            suggestion.TimeframeMinutes,
            suggestion.Side,
            suggestion.Size,
            suggestion.EntryPrice,
            suggestion.StopPrice,
            suggestion.TargetPrice,
            suggestion.Mode,
            suggestion.State,
            suggestion.CreatedAt,
            RatioOf(suggestion.EntryPrice, suggestion.StopPrice, suggestion.TargetPrice),
            MoneyOf(spec, suggestion.EntryPrice, suggestion.StopPrice, suggestion.Size),
            MoneyOf(spec, suggestion.EntryPrice, suggestion.TargetPrice, suggestion.Size),
            suggestion.Rationale,
            suggestion.CitedIndicator,
            suggestion.CitedPeriod,
            suggestion.CitedResolutionMinutes,
            suggestion.Confidence,
            suggestion.ExpiresAt);
    }

    // The wireframe's dollar risk/reward, computed SERVER-side from the single shipped money-math seam
    // (InstrumentSpec.LossPerContract) rather than in a browser. Null when the instrument has no configured spec:
    // this is a DISPLAY figure, so omitting it degrades the card rather than failing the read -- the take path
    // (gh#548) is where a missing spec must fail closed, because there a wrong number moves real size.
    private static decimal? MoneyOf(InstrumentContractSpec? spec, decimal entry, decimal against, int size) =>
        spec is null ? null : spec.Spec.LossPerContract(new Price(entry), new Price(against)) * size;

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

/// <summary>
/// The body of a pass (gh#547, R-4). <b>Both fields are optional</b>: a pass is a <b>neutral decline</b>, not a
/// rejection, so the operator need give no reason at all.
/// </summary>
/// <param name="Reasons">
/// The pass reasons, a <c>[Flags]</c> multi-select. <see cref="SuggestionPassReason.None"/> (the default) is a valid,
/// neutral pass.
/// </param>
/// <param name="Note">An optional free-text note, capped at <see cref="SuggestionDisposition.NoteMaxLength"/>.</param>
public sealed record SuggestionPassRequest(
    SuggestionPassReason Reasons = SuggestionPassReason.None,
    string? Note = null);

/// <summary>A recorded disposition (gh#547) — the operator's neutral pass, as written to the journal.</summary>
/// <param name="SuggestionId">The suggestion that was disposed.</param>
/// <param name="Kind">The operator act — <see cref="SuggestionDispositionKind.Passed"/> on this route.</param>
/// <param name="Reasons">The pass reasons (may be <see cref="SuggestionPassReason.None"/>).</param>
/// <param name="Note">The operator's note, or <see langword="null"/>.</param>
/// <param name="CreatedAt">When the disposition was recorded.</param>
public sealed record SuggestionDispositionResponse(
    Guid SuggestionId,
    SuggestionDispositionKind Kind,
    SuggestionPassReason Reasons,
    string? Note,
    DateTimeOffset CreatedAt)
{
    /// <summary>Projects a persisted disposition into its API view.</summary>
    /// <param name="disposition">The persisted row.</param>
    /// <returns>The response.</returns>
    public static SuggestionDispositionResponse From(SuggestionDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        return new SuggestionDispositionResponse(
            disposition.SuggestionId,
            disposition.Kind,
            disposition.Reasons,
            disposition.Note,
            disposition.CreatedAt);
    }
}
