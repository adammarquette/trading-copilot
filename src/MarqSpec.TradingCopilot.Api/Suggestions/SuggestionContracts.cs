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
/// <param name="StateChangedAt">
/// When <see cref="State"/> last changed (gh#545) — <see langword="null"/> while the suggestion is still in the
/// <see cref="SuggestionState.Active"/> state it was issued in, so the card can show <i>when</i> a suggestion went
/// stale or void, not merely that it did.
/// </param>
/// <param name="Version">
/// This suggestion's version along its supersede chain (gh#550) — <c>1</c> for a first issuance, one higher for each
/// re-formed setup. Immutable once issued.
/// </param>
/// <param name="SupersedesId">
/// The suggestion this one supersedes (gh#550), or <see langword="null"/> for the first version — the link a client
/// follows to walk the chain by id back through its history.
/// </param>
/// <param name="Disposition">
/// The operator's recorded disposition (gh#547 pass / gh#549 take) with its deviations, present only on the get-by-id
/// read and only once the operator has acted; <see langword="null"/> on the list read and before any disposition.
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
    DateTimeOffset ExpiresAt,
    DateTimeOffset? StateChangedAt,
    int Version,
    Guid? SupersedesId,
    SuggestionDispositionResponse? Disposition = null)
{
    /// <summary>Projects a persisted suggestion into its API view.</summary>
    /// <param name="suggestion">The persisted row.</param>
    /// <param name="spec">
    /// The instrument's contract facts (gh#541) used to money-value the geometry, or <see langword="null"/> when the
    /// instrument is not configured — in which case the dollar figures are omitted rather than guessed.
    /// </param>
    /// <param name="disposition">
    /// The suggestion's recorded disposition (gh#547 / gh#549), surfaced on the get-by-id read so the client can render
    /// the taken-vs-suggested deviations; <see langword="null"/> on the list read and when the operator has not yet acted.
    /// </param>
    /// <returns>The response.</returns>
    public static SuggestionResponse From(
        Suggestion suggestion, InstrumentContractSpec? spec = null, SuggestionDisposition? disposition = null)
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
            suggestion.ExpiresAt,
            suggestion.StateChangedAt,
            suggestion.Version,
            suggestion.SupersedesId,
            disposition is null ? null : SuggestionDispositionResponse.From(disposition));
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

/// <summary>
/// The body of a take (gh#548, R-11b / R-12). Its <b>only</b> field is the current market reference — the two
/// numbers a server-originated proposal cannot invent (the tick size, point value and safety-stop distance) come
/// from the instrument-spec source, never the client (that is ruled out in writing on <see cref="Orders.SendOrderRequest"/>).
/// </summary>
/// <param name="ReferencePrice">
/// The caller's current market price, as on every order path (the server fetches no venue quote). It does double
/// duty: the take-time <b>drift re-check</b> re-measures it against the suggestion's entry tolerance <b>now</b>
/// (R-12, not the eventually-consistent <see cref="SuggestionState.Stale"/> flag), and it is the fat-finger band's
/// reference once the ticket reaches the gate (R-16). Must be positive.
/// </param>
public sealed record SuggestionTakeRequest(decimal ReferencePrice);

/// <summary>
/// A recorded disposition (gh#547 pass, gh#549 take) — the operator's act on a suggestion, as written to the journal.
/// </summary>
/// <param name="SuggestionId">The suggestion that was disposed.</param>
/// <param name="Kind">
/// The operator act — <see cref="SuggestionDispositionKind.Passed"/> on the pass route,
/// <see cref="SuggestionDispositionKind.Taken"/> / <see cref="SuggestionDispositionKind.Modified"/> on the take route.
/// </param>
/// <param name="Reasons">The pass reasons (may be <see cref="SuggestionPassReason.None"/>; always <c>None</c> for a take).</param>
/// <param name="Deviations">
/// For a <see cref="SuggestionDispositionKind.Modified"/> take, which fields the operator changed (gh#549);
/// <see cref="SuggestionDeviation.None"/> for a <c>Taken</c> or a <c>Passed</c> disposition.
/// </param>
/// <param name="TakenEntryPrice">The entry actually submitted at take time, or <see langword="null"/> for a pass. The suggested value is on the parent <see cref="SuggestionResponse"/>, so the client renders the was → now delta per field.</param>
/// <param name="TakenStopPrice">The working (protective) stop actually submitted at take time, or <see langword="null"/> for a pass.</param>
/// <param name="TakenTargetPrice">The take-profit actually submitted at take time; <see langword="null"/> when the take carried none, or for a pass.</param>
/// <param name="TakenSize">The size actually submitted at take time, or <see langword="null"/> for a pass.</param>
/// <param name="Note">The operator's note, or <see langword="null"/>.</param>
/// <param name="CreatedAt">When the disposition was recorded.</param>
public sealed record SuggestionDispositionResponse(
    Guid SuggestionId,
    SuggestionDispositionKind Kind,
    SuggestionPassReason Reasons,
    SuggestionDeviation Deviations,
    decimal? TakenEntryPrice,
    decimal? TakenStopPrice,
    decimal? TakenTargetPrice,
    int? TakenSize,
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
            disposition.Deviations,
            disposition.TakenEntryPrice,
            disposition.TakenStopPrice,
            disposition.TakenTargetPrice,
            disposition.TakenSize,
            disposition.Note,
            disposition.CreatedAt);
    }
}
