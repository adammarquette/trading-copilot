using System.ComponentModel.DataAnnotations.Schema;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A persisted AI trade suggestion (data dictionary §6) — the <b>spine</b> only (gh#7): identity, the proposed
/// trade, its mode, and its lifecycle state.
/// </summary>
/// <remarks>
/// <para>
/// The spine gained the <b>rationale + cited signal</b> (gh#542), a <b>confidence</b> (gh#543) and a
/// <b>validity window</b> (gh#544). Still deferred: strategy linkage (A5 owns the <c>Strategy</c> entity),
/// retrieval references (VEC — lands with its retrieval consumer), version / supersedes, and dispositions.
/// </para>
/// <para>
/// <see cref="Mode"/> is <b>mode-guarded</b> (R-14): a DB constraint trigger refuses a suggestion whose mode
/// disagrees with its account's persisted mode at write time, and <see cref="TradingMode.Undeclared"/> is
/// refused outright by a check constraint — an undeclared account cannot be traded, so nothing should ever be
/// suggested on it.
/// </para>
/// </remarks>
public class Suggestion : IUserOwned
{
    /// <summary>The suggestion's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20). Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The account the suggestion targets — the parent whose mode the R-14 guard compares against.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The venue contract the suggestion is for (e.g. <c>CON.F.US.MES.U26</c>).</summary>
    public required string Instrument { get; set; }

    /// <summary>The proposed direction. <c>required</c>: the zero value is a real side, never a default.</summary>
    public required OrderSide Side { get; set; }

    /// <summary>The proposed size in contracts. Positive — a DB check refuses the rest.</summary>
    public required int Size { get; set; }

    /// <summary>The proposed entry price.</summary>
    public required decimal EntryPrice { get; set; }

    /// <summary>The proposed protective stop price.</summary>
    public required decimal StopPrice { get; set; }

    /// <summary>The proposed target price (a single target in the spine; ladders come with R-4).</summary>
    public required decimal TargetPrice { get; set; }

    /// <summary>
    /// The mode the suggestion was issued under (R-14). Guarded: must equal the account's persisted mode at
    /// write time, and <see cref="TradingMode.Undeclared"/> is refused outright.
    /// </summary>
    public required TradingMode Mode { get; set; }

    /// <summary>The lifecycle state. <see cref="SuggestionState.Unknown"/> is refused by a DB check.</summary>
    public required SuggestionState State { get; set; }

    /// <summary>When the suggestion was issued.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The model's plain-language rationale (gh#542, R-4). Length-capped, and validated at the reviewer's parse
    /// boundary — an over-long rationale fails closed to <c>MalformedOutput</c> rather than being truncated here.
    /// </summary>
    /// <remarks>
    /// <b>Untrusted display data.</b> It is model-authored prose: never re-injected into a later prompt as
    /// instruction (in particular it must never enter the deep-review enrichment, gh#476) and never rendered as
    /// markup. Empty string, never null, so a reader needs no null check.
    /// </remarks>
    public required string Rationale { get; set; }

    /// <summary>
    /// The firing that produced this suggestion (gh#542) — a soft link; the journal outlives the trigger, so the
    /// citation below is <b>copied</b> rather than resolved through it.
    /// </summary>
    public Guid? TriggerFiringId { get; set; }

    /// <summary>
    /// The R-22 indicator that fired, <b>copied at issuance</b> (gh#542). Copied rather than joined because
    /// indicator / period / resolution live on the mutable, deletable <c>TriggerRecord</c> while R-4 requires the
    /// cited signal to stay readable forever — the same reason <c>TriggerFiringRecord</c> copies threshold and
    /// comparison.
    /// </summary>
    public required string CitedIndicator { get; set; }

    /// <summary>The fired indicator's period, copied at issuance (gh#542).</summary>
    public required int CitedPeriod { get; set; }

    /// <summary>The bar size the fired indicator was computed over, copied at issuance (gh#542).</summary>
    public required int CitedResolutionMinutes { get; set; }

    /// <summary>
    /// The model's confidence, 0–100 (gh#543, R-4). A DB check pins the range; the reviewer fails closed on a
    /// missing or out-of-range value.
    /// </summary>
    /// <remarks>
    /// <b>Display only.</b> It never influences size (which is the operator's trigger's), geometry validation, the
    /// risk gate, or whether the row is written at all — a low-confidence proposal still persists, because the
    /// operator decides and the model does not get to self-censor by number.
    /// </remarks>
    public required int Confidence { get; set; }

    /// <summary>
    /// When the suggestion stops being actionable (gh#544, R-4) — the system's value, never the model's, computed by
    /// <c>SuggestionValidity</c> and clamped so it can never outlive the session's auto-flatten deadline (R-13).
    /// A DB check pins it strictly after <see cref="CreatedAt"/>.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// The suggestion's <b>headline timeframe</b> in minutes (R-4, gh#592) — the bar size it is framed on, so a
    /// reader can tell a scalp from a swing. In the single-cited-signal model that exists today it <b>is</b> the
    /// cited indicator's resolution (Adam, 2026-08-02), so this is a computed accessor over
    /// <see cref="CitedResolutionMinutes"/> — <b>never a second stored column</b>, no duplicate source of truth.
    /// <c>[NotMapped]</c>, so it adds no schema. The "smallest of several aligning timeframes, larger as supporting
    /// confluence" case needs a multi-signal model that does not exist yet — that is gh#593.
    /// </summary>
    [NotMapped]
    public int TimeframeMinutes => CitedResolutionMinutes;
}
