namespace MarqSpec.TradingCopilot.Domain.Signals;

/// <summary>
/// One operator's accumulated, recency-decayed salience weights (gh#27, ADR-0014) — their stars and mutes
/// aggregated into a signed weight per <c>(dimension, value)</c>. Built as a <b>pure function</b> of the operator's
/// feedback and a supplied <c>now</c> (no clock, no store), so it is deterministic and re-runnable: the
/// same feedback at the same instant always yields the same profile. Star weights are positive, mute weights
/// negative; each is scaled by an exponential recency decay, so what the operator cared about <i>lately</i> weighs
/// most and old feedback fades toward zero.
/// </summary>
public sealed class SalienceProfile
{
    private readonly Dictionary<(SalienceDimension Dimension, string Value), double> _weights;

    private SalienceProfile(Dictionary<(SalienceDimension, string), double> weights) => _weights = weights;

    /// <summary>Builds the profile from one operator's ratings as of <paramref name="now"/>.</summary>
    /// <param name="ratings">The operator's star/mute feedback, each joined to its rated item's dimensions.</param>
    /// <param name="now">The instant to decay against — supplied by the caller; the profile never reads a clock.</param>
    /// <param name="parameters">The salience tuning (half-life, per-dimension weights).</param>
    /// <returns>The aggregated, decayed profile.</returns>
    public static SalienceProfile Build(
        IReadOnlyList<SoftSignalRating> ratings, DateTimeOffset now, SalienceParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(ratings);
        ArgumentNullException.ThrowIfNull(parameters);

        Dictionary<(SalienceDimension, string), double> weights = [];
        foreach (SoftSignalRating rating in ratings)
        {
            // Importance ALONE feeds salience (ADR-0014, gh#762): only the Importance-axis kinds (Star / Mute)
            // contribute. The Direction axis (ThumbsUp / ThumbsDown) is salience-INERT by construction — it feeds R-9
            // learning + the read surface, never what surfaces — so a direction rating is skipped here, keeping the two
            // axes orthogonal in effect as well as in storage. Anything else (the store-refused Unknown, or any future
            // non-importance kind) is likewise skipped, so a new kind can never silently leak into surfacing.
            if (rating.Kind is not (SoftSignalKind.Star or SoftSignalKind.Mute))
            {
                continue;
            }

            double sign = rating.Kind == SoftSignalKind.Star ? 1.0 : -1.0;

            double decay = RecencyDecay(rating.RatedAt, now, parameters.HalfLifeDays);
            if (decay <= 0.0)
            {
                continue;
            }

            NewsDimensions dimensions = rating.Dimensions;
            Accumulate(weights, SalienceDimension.Instrument, dimensions.Instruments, sign * parameters.InstrumentWeight * decay);
            Accumulate(weights, SalienceDimension.Topic, dimensions.Topics, sign * parameters.TopicWeight * decay);
            Accumulate(weights, SalienceDimension.Source, dimensions.Sources, sign * parameters.SourceWeight * decay);
        }

        return new SalienceProfile(weights);
    }

    /// <summary>The accumulated signed weight for one <c>(dimension, value)</c>, or 0 if the operator's feedback never touched it.</summary>
    /// <param name="dimension">The dimension to look up.</param>
    /// <param name="value">The value within that dimension (for example the instrument <c>ES</c>).</param>
    public double WeightFor(SalienceDimension dimension, string value) =>
        _weights.TryGetValue((dimension, value), out double weight) ? weight : 0.0;

    /// <summary>True when no feedback contributed — a cold-start operator, whose every item scores at base relevance.</summary>
    public bool IsEmpty => _weights.Count == 0;

    private static void Accumulate(
        Dictionary<(SalienceDimension, string), double> weights,
        SalienceDimension dimension,
        IReadOnlyList<string> values,
        double amount)
    {
        foreach (string value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            weights.TryGetValue((dimension, value), out double current);
            weights[(dimension, value)] = current + amount;
        }
    }

    // 2^(-age/halfLife): 1.0 at zero age, 0.5 at one half-life. A future-dated rating (clock skew) clamps to age 0,
    // and a non-positive half-life disables decay so a star never fades.
    private static double RecencyDecay(DateTimeOffset ratedAt, DateTimeOffset now, double halfLifeDays)
    {
        if (halfLifeDays <= 0.0)
        {
            return 1.0;
        }

        double ageDays = Math.Max(0.0, (now - ratedAt).TotalDays);
        return Math.Pow(2.0, -ageDays / halfLifeDays);
    }
}
