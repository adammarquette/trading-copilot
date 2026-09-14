namespace MarqSpec.TradingCopilot.Domain.Signals;

/// <summary>
/// The similarity dimensions resolved onto one news item (gh#27) — the axes a star generalizes along. Sourced from
/// the item's gh#359 relevance (instruments, topics) plus its provenance (sources). Empty lists are fine: an item
/// that shares no dimension with any of the operator's stars simply scores at base.
/// </summary>
/// <param name="Instruments">The traded instruments the item bears on (normalized, as stored on the news row).</param>
/// <param name="Topics">The topics the item matched.</param>
/// <param name="Sources">The source feeds that carried the item.</param>
/// <param name="SemanticSimilarity">
/// The item's embedding-neighbourhood similarity to the operator's stars in <c>[0, 1]</c> (gh#853) — the max cosine
/// similarity to the nearest starred item's vector, supplied by the caller (it already ranked this item against the
/// stars). <see langword="null"/> when the semantic axis is unavailable or off, so it simply contributes nothing and
/// the scorer degrades to the categorical dimensions; a trailing optional so positional construction still compiles.
/// </param>
public sealed record NewsDimensions(
    IReadOnlyList<string> Instruments,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> Sources,
    double? SemanticSimilarity = null);

/// <summary>
/// One piece of the operator's feedback joined to the dimensions of the item it rated (gh#27) — the unit the
/// <see cref="SalienceProfile"/> aggregates. It carries the rated item's dimensions (not the item itself), so the
/// profile stays a pure function of the feedback and needs no store to score a candidate.
/// </summary>
/// <param name="Kind">Star (raises) or mute (lowers).</param>
/// <param name="Dimensions">The rated item's dimensions — the axes the rating generalizes along.</param>
/// <param name="RatedAt">When the operator rated it — the age input to recency decay.</param>
public sealed record SoftSignalRating(
    SoftSignalKind Kind,
    NewsDimensions Dimensions,
    DateTimeOffset RatedAt);

/// <summary>
/// The tuning of the salience model (gh#27, ADR-0014) — every knob the pure scorer needs, carrying the deployment
/// defaults. Held in the domain (rather than read from config) so the math stays a pure function of its inputs; the
/// API's <c>SalienceOptions</c> binds configuration onto this.
/// </summary>
/// <param name="HalfLifeDays">The recency half-life: a star this many days old counts half as much (exponential decay). Non-positive disables decay.</param>
/// <param name="MultiplierCap">The most a stack of stars can raise an item — the upper clamp.</param>
/// <param name="MultiplierFloor">The least a stack of mutes can lower an item — kept <b>above zero</b>, so a muted item is down-weighted but never hidden (no hard filter; the ADR-0014 salience floor).</param>
/// <param name="InstrumentWeight">Per-star weight contributed along a shared instrument.</param>
/// <param name="TopicWeight">Per-star weight contributed along a shared topic.</param>
/// <param name="SourceWeight">Per-star weight contributed along a shared source (weaker than instrument/topic).</param>
/// <param name="SemanticWeight">Weight on the semantic-embedding axis (gh#853): the item's max cosine similarity to a star, scaled by this, is its semantic contribution. A trailing default so positional construction still compiles.</param>
public sealed record SalienceParameters(
    double HalfLifeDays = 14.0,
    double MultiplierCap = 5.0,
    double MultiplierFloor = 0.25,
    double InstrumentWeight = 1.0,
    double TopicWeight = 1.0,
    double SourceWeight = 0.5,
    double SemanticWeight = 1.0);

/// <summary>
/// One reason an item was reweighted (gh#27) — the explainability ADR-0014 requires, so weighting is never a hidden
/// score. The dimension + value the operator's feedback and this item share, and how much it moved the multiplier
/// (positive from stars, negative from mutes; decayed, so recent feedback weighs more).
/// </summary>
/// <param name="Dimension">The shared dimension (instrument / topic / source).</param>
/// <param name="Value">The shared value (for example <c>ES</c> or <c>fomc</c>).</param>
/// <param name="Contribution">Its signed contribution to the multiplier.</param>
public sealed record SalienceReason(SalienceDimension Dimension, string Value, double Contribution);

/// <summary>
/// The personalized weighting of one item for one operator (gh#27): a <see cref="Multiplier"/> on the item's base
/// relevance, clamped to the configured floor/cap, with the <see cref="Reasons"/> that explain it. A cold-start
/// (no feedback) scores <see cref="Multiplier"/> = 1 with no reasons — exactly the base relevance.
/// </summary>
/// <param name="Multiplier">The clamped multiplier on base relevance (1 = unchanged).</param>
/// <param name="Reasons">The shared dimensions that moved it, ranked by absolute contribution (empty when unchanged).</param>
public sealed record SalienceScore(double Multiplier, IReadOnlyList<SalienceReason> Reasons);
