using MarqSpec.TradingCopilot.Data.Tenancy;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One factor a <see cref="Suggestion"/> cites (data dictionary §6, R-4, gh#729, ADR-0026) — a member of the
/// suggestion's <b>cited-factor set</b>. The set has exactly one <see cref="IsPrimary"/> factor (the headline) and
/// zero-or-more supporting; the primary is the single smallest-timeframe factor (gh#592's min-rule, derived by
/// <see cref="Domain.Suggestions.CitedFactorSet.DerivePrimary{T}"/> — never hand-set). Today every suggestion is the
/// degenerate <b>N=1</b> case: one primary <see cref="CitedFactorKind.Indicator"/> factor, no supporting — exactly
/// the single-cited-signal behaviour that used to live in three columns on the suggestion itself (gh#592).
/// </summary>
/// <remarks>
/// <para>
/// <b>Operator-owned</b> (<see cref="IUserOwned"/>): a factor is a child of an owned suggestion, so the R-20
/// default-deny filter (ADR-0011) must reach it too — an <see cref="System.Linq"/> <c>Include</c> of the set is
/// scoped to the caller, never another operator's rows. The owner is always the suggestion's owner.
/// </para>
/// <para>
/// The two arms are mutually exclusive, tied to <see cref="Kind"/> by a DB pairing check. The
/// <see cref="CitedFactorKind.Indicator"/> arm copies the fired read (<see cref="Indicator"/> / <see cref="Period"/>)
/// exactly as the old <c>Suggestion.CitedIndicator</c> did, and for the same reason: it lives on the mutable,
/// deletable <c>TriggerRecord</c> while R-4 needs the citation readable forever. The
/// <see cref="CitedFactorKind.Level"/> arm is a <b>snapshot copy</b> of a <see cref="PriceLevel"/> — a
/// <see cref="LevelId"/> soft reference (no FK) plus the zone's values frozen at issuance, because a level is
/// re-scored and evicted and R-4 forbids the citation moving with it.
/// </para>
/// </remarks>
public sealed class CitedFactor : IUserOwned
{
    /// <summary>The longest a cited <see cref="Indicator"/> name may be — matches <c>Suggestion.CitedIndicator</c>.</summary>
    public const int IndicatorMaxLength = 32;

    /// <summary>The longest a snapshotted <see cref="LevelVenue"/> may be — matches <c>PriceLevel.Venue</c>.</summary>
    public const int LevelVenueMaxLength = 64;

    /// <summary>The factor's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20) — always the suggestion's owner. Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The suggestion this factor is cited by. Cascade child: a factor never outlives its suggestion.</summary>
    public Guid SuggestionId { get; set; }

    /// <summary>What the factor cites. <see cref="CitedFactorKind.Unknown"/> is refused by a DB check.</summary>
    public required CitedFactorKind Kind { get; set; }

    /// <summary>
    /// Whether this is the suggestion's <b>primary</b> (headline) factor — the single smallest-timeframe one
    /// (gh#592). Derived by <see cref="Domain.Suggestions.CitedFactorSet.DerivePrimary{T}"/> at issuance, never
    /// hand-set. A DB partial unique index pins <b>at most one</b> primary per suggestion; <see langword="false"/>
    /// (supporting) is the common value and is unconstrained in number.
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// The timeframe this factor is read on, in minutes — the axis the min-rule ranks the set by (gh#592) and, for
    /// the primary, the suggestion's headline timeframe. Positive; a DB check refuses the rest.
    /// </summary>
    public required int TimeframeMinutes { get; set; }

    // ---- Indicator arm (Kind = Indicator): non-null here, and the level columns all null ----

    /// <summary>The R-22 indicator that fired, copied at issuance (gh#592). Non-null iff <see cref="Kind"/> is Indicator.</summary>
    public string? Indicator { get; set; }

    /// <summary>The fired indicator's period, copied at issuance. Non-null iff <see cref="Kind"/> is Indicator.</summary>
    public int? Period { get; set; }

    // ---- Level-snapshot arm (Kind = Level): non-null here, and the indicator columns null. A COPY, not an FK ----

    /// <summary>The source <see cref="PriceLevel"/>'s id — a <b>soft reference</b>, deliberately with no FK, so a re-scored or evicted level never disturbs the citation (R-4).</summary>
    public Guid? LevelId { get; set; }

    /// <summary>The snapshotted venue the level was found on. Non-null iff <see cref="Kind"/> is Level.</summary>
    public string? LevelVenue { get; set; }

    /// <summary>The snapshotted <see cref="PriceLevelKind"/> as its int value. Non-null and non-zero iff <see cref="Kind"/> is Level.</summary>
    public int? LevelKind { get; set; }

    /// <summary>The snapshotted top of the zone band. Non-null and strictly above <see cref="LevelBottom"/> iff <see cref="Kind"/> is Level.</summary>
    public decimal? LevelTop { get; set; }

    /// <summary>The snapshotted bottom of the zone band. Non-null iff <see cref="Kind"/> is Level.</summary>
    public decimal? LevelBottom { get; set; }

    /// <summary>The snapshotted detector significance (gh#597). Non-null iff <see cref="Kind"/> is Level.</summary>
    public decimal? LevelSignificance { get; set; }
}
