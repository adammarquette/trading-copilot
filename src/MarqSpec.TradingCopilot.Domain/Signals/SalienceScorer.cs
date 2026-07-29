namespace MarqSpec.TradingCopilot.Domain.Signals;

/// <summary>
/// Scores one news item's personalized salience for one operator (gh#27, ADR-0014): given their
/// <see cref="SalienceProfile"/> and the item's dimensions, a <b>multiplier on base relevance</b> plus the reasons
/// that explain it. Pure and deterministic.
/// </summary>
/// <remarks>
/// This is a <b>soft salience weight only</b> — it reorders and reweights what surfaces. It is structurally
/// unreachable from the risk gate or order sizing: nothing in this namespace is referenced by
/// <c>MarqSpec.TradingCopilot.Domain.Risk</c> or the execution path (ADR-0007, enforcement stays below the model).
/// That separation is the single invariant gh#27 / QA #362 exist to guard — a star can change what the operator
/// <i>sees first</i>, never a limit, a size, or a gate decision.
/// </remarks>
public static class SalienceScorer
{
    /// <summary>Scores an item's salience multiplier against an operator's profile.</summary>
    /// <param name="profile">The operator's accumulated, decayed weights.</param>
    /// <param name="item">The item's similarity dimensions.</param>
    /// <param name="parameters">The salience tuning (the floor/cap clamp).</param>
    /// <returns>The clamped multiplier (1 = base) and the ranked reasons behind it.</returns>
    public static SalienceScore Score(SalienceProfile profile, NewsDimensions item, SalienceParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(parameters);

        List<SalienceReason> reasons = [];
        CollectReasons(reasons, profile, SalienceDimension.Instrument, item.Instruments);
        CollectReasons(reasons, profile, SalienceDimension.Topic, item.Topics);
        CollectReasons(reasons, profile, SalienceDimension.Source, item.Sources);

        // The multiplier is 1 (base) plus the summed signed contributions of every dimension the item shares with the
        // operator's feedback, clamped so a stack of stars can't run away (cap) and a stack of mutes can't hide a
        // material item (floor > 0). A cold-start or unrelated item shares nothing, so it scores exactly 1.
        double raw = 1.0 + reasons.Sum(reason => reason.Contribution);
        double multiplier = Math.Clamp(raw, parameters.MultiplierFloor, parameters.MultiplierCap);

        // Explain most-influential first; the value tiebreak keeps the order deterministic for equal contributions.
        IReadOnlyList<SalienceReason> ranked =
        [
            .. reasons
                .OrderByDescending(reason => Math.Abs(reason.Contribution))
                .ThenBy(reason => reason.Value, StringComparer.Ordinal),
        ];

        return new SalienceScore(multiplier, ranked);
    }

    private static void CollectReasons(
        List<SalienceReason> reasons, SalienceProfile profile, SalienceDimension dimension, IReadOnlyList<string> values)
    {
        foreach (string value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            double weight = profile.WeightFor(dimension, value);
            if (weight != 0.0)
            {
                reasons.Add(new SalienceReason(dimension, value, weight));
            }
        }
    }
}
