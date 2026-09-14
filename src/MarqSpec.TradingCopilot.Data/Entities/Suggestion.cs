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
/// The spine gained the <b>rationale + cited signal</b> (gh#542), a <b>confidence</b> (gh#543), a
/// <b>validity window</b> (gh#544) and now <b>version / supersedes</b> (gh#550) — a re-formed setup issues a
/// <b>superseding</b> suggestion, versioned and immutable once issued (R-4, ADR-0013). Still deferred: strategy
/// linkage (A5 owns the <c>Strategy</c> entity), retrieval references (VEC — lands with its retrieval consumer),
/// and dispositions.
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

    /// <summary>
    /// The <b>venue-neutral</b> instrument symbol (e.g. <c>ES</c>) — <b>not</b> a venue contract key. The front-month
    /// contract resolves at take time exactly as an order does, and both the spec source (gh#541) and the venue key
    /// on this symbol, so the take path (gh#548) resolves the tick size and safety-stop distance from it.
    /// </summary>
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
    /// citation below is <b>copied</b> rather than resolved through it. <see langword="null"/> on a producer that
    /// cites no firing; read <see cref="Origin"/> to learn which producer that was, never this column's absence.
    /// </summary>
    public Guid? TriggerFiringId { get; set; }

    /// <summary>
    /// <b>Which producer staged this row</b> (gh#1134, R-4 / R-6) — the trigger scan, or the chat co-pilot. A DB check
    /// refuses the <see cref="SuggestionOrigin.Unknown"/> zero, so a row whose producer nobody set never persists.
    /// </summary>
    /// <remarks>
    /// Stored rather than inferred from a null <see cref="TriggerFiringId"/>: an absence is not provenance, and the
    /// operator's card needs to say what it is showing rather than guess. <b>Provenance, not permission</b> — the
    /// execution path does not read it, and a chat proposal is taken through the identical gate.
    /// </remarks>
    public required SuggestionOrigin Origin { get; set; }

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
    /// When the lifecycle <see cref="State"/> last changed (gh#545) — ADR-0013's invariant that <i>every recovery
    /// transition is audited</i>, with nowhere to land until now. <c>null</c> while the suggestion is still in the
    /// <see cref="SuggestionState.Active"/> state it was issued in; otherwise stamped by whichever writer transitions
    /// it (the expire sweep, the drift consumer, the take path), written <b>atomically</b> with the state change so
    /// the audit can never lag the transition it records.
    /// </summary>
    public DateTimeOffset? StateChangedAt { get; set; }

    /// <summary>
    /// This suggestion's version along its supersede chain (gh#550, R-4). The first suggestion for a setup is
    /// <c>1</c>; a re-formed setup issues a <b>superseding</b> row one version higher (see <see cref="SupersedesId"/>).
    /// </summary>
    /// <remarks>
    /// Versioned/immutable-once-issued is R-4's journal-integrity rule: an issued suggestion's trade parameters never
    /// change — a re-formed setup issues a new superseding row rather than mutating this one. Defaults to <c>1</c>
    /// (both a CLR initializer and the DB default), so a first issuance need not set it explicitly.
    /// </remarks>
    public int Version { get; set; } = 1;

    /// <summary>
    /// The suggestion this one supersedes (gh#550, R-4), or <see langword="null"/> for the first version of a setup —
    /// a self-reference forming the supersede chain, whose <b>head</b> is the row nothing else supersedes.
    /// </summary>
    /// <remarks>
    /// The FK is <c>OnDelete: Restrict</c>: a superseded row can never be deleted while a later version still points
    /// at it, so the lineage the R-9 learning loop reads never silently vanishes. Set once at issuance, never changed.
    /// </remarks>
    public Guid? SupersedesId { get; set; }

    /// <summary>
    /// The <b>cited-factor set</b> (gh#729, ADR-0026, R-4) — why this suggestion fired. Exactly one factor is the
    /// <see cref="CitedFactor.IsPrimary"/> headline (the smallest timeframe, gh#592), the rest supporting. Today
    /// every suggestion is the degenerate N=1 case: one primary <see cref="CitedFactorKind.Indicator"/> factor, no
    /// supporting — the single-cited-signal behaviour that superseded the old <c>CitedIndicator</c> /
    /// <c>CitedPeriod</c> / <c>CitedResolutionMinutes</c> columns. Cascade child of this suggestion.
    /// </summary>
    public ICollection<CitedFactor> CitedFactors { get; set; } = [];
}
