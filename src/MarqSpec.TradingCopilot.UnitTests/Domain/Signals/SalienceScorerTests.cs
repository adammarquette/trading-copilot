using MarqSpec.TradingCopilot.Domain.Signals;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Signals;

/// <summary>
/// The pure salience scorer + profile (gh#27, ADR-0014). The properties that matter: a star raises only <b>similar</b>
/// items (no cross-contamination), an unrelated / cold-start item scores exactly base (multiplier 1), recency decay
/// fades old feedback, a mute lowers but the <b>floor</b> keeps the item visible (never a hard filter), the <b>cap</b>
/// bounds a stack of stars, un-starring round-trips to base, and every reweight is explainable. Deterministic — a
/// pure function of (feedback, now).
/// </summary>
public class SalienceScorerTests
{
    private static readonly SalienceParameters _params = new(); // 14d half-life, cap 5, floor 0.25, inst 1, topic 1, source 0.5
    private static readonly DateTimeOffset _now = new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);

    private static NewsDimensions Dims(
        IReadOnlyList<string>? instruments = null,
        IReadOnlyList<string>? topics = null,
        IReadOnlyList<string>? sources = null) =>
        new(instruments ?? [], topics ?? [], sources ?? []);

    private static SoftSignalRating Star(NewsDimensions dims, DateTimeOffset? at = null) =>
        new(SoftSignalKind.Star, dims, at ?? _now);

    private static SoftSignalRating Mute(NewsDimensions dims, DateTimeOffset? at = null) =>
        new(SoftSignalKind.Mute, dims, at ?? _now);

    private static SalienceScore Score(IReadOnlyList<SoftSignalRating> feedback, NewsDimensions item) =>
        SalienceScorer.Score(SalienceProfile.Build(feedback, _now, _params), item, _params);

    // Score an item against a cold-start (no-feedback) profile -- isolates the caller-supplied semantic axis, which
    // rides on the item and is independent of the aggregated star/mute profile (gh#853).
    private static SalienceScore ScoreSemantic(NewsDimensions item, SalienceParameters? parameters = null) =>
        SalienceScorer.Score(SalienceProfile.Build([], _now, parameters ?? _params), item, parameters ?? _params);

    [Fact]
    public void ColdStart_ScoresExactlyBase()
    {
        SalienceScore score = Score([], Dims(instruments: ["ES"]));

        score.Multiplier.Should().Be(1.0);
        score.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void AStar_RaisesASimilarItem()
    {
        SalienceScore score = Score([Star(Dims(instruments: ["ES"]))], Dims(instruments: ["ES"]));

        score.Multiplier.Should().BeGreaterThan(1.0);
        score.Reasons.Should().ContainSingle(reason =>
            reason.Dimension == SalienceDimension.Instrument && reason.Value == "ES");
    }

    [Fact]
    public void AStar_LeavesAnUnrelatedItemAtBase()
    {
        // Star an ES item; an unrelated NQ item is unchanged -- no cross-contamination.
        SalienceScore score = Score([Star(Dims(instruments: ["ES"]))], Dims(instruments: ["NQ"]));

        score.Multiplier.Should().Be(1.0);
        score.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void Contributions_AccumulateAcrossDimensions()
    {
        SoftSignalRating[] feedback = [Star(Dims(instruments: ["ES"], topics: ["fomc"], sources: ["finnhub"]))];

        SalienceScore all = Score(feedback, Dims(instruments: ["ES"], topics: ["fomc"], sources: ["finnhub"]));
        SalienceScore instrumentOnly = Score(feedback, Dims(instruments: ["ES"]));

        all.Multiplier.Should().BeGreaterThan(instrumentOnly.Multiplier);
        all.Reasons.Should().HaveCount(3);
    }

    [Fact]
    public void Source_IsAWeakerSignalThanInstrument()
    {
        SalienceScore instrument = Score([Star(Dims(instruments: ["ES"]))], Dims(instruments: ["ES"]));
        SalienceScore source = Score([Star(Dims(sources: ["finnhub"]))], Dims(sources: ["finnhub"]));

        source.Multiplier.Should().BeLessThan(instrument.Multiplier); // SourceWeight 0.5 < InstrumentWeight 1.0
        source.Multiplier.Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void RecencyDecay_FadesAnOlderStar()
    {
        NewsDimensions es = Dims(instruments: ["ES"]);

        SalienceScore fresh = Score([Star(es, _now)], es);
        SalienceScore old = Score([Star(es, _now.AddDays(-28))], es); // two half-lives

        old.Multiplier.Should().BeLessThan(fresh.Multiplier);
        old.Multiplier.Should().BeGreaterThan(1.0); // faded, not gone
    }

    [Fact]
    public void RecencyDecay_OneHalfLife_HalvesTheContribution()
    {
        NewsDimensions es = Dims(instruments: ["ES"]);

        double fresh = Score([Star(es, _now)], es).Multiplier - 1.0;
        double aHalfLifeOld = Score([Star(es, _now.AddDays(-14))], es).Multiplier - 1.0;

        aHalfLifeOld.Should().BeApproximately(fresh / 2.0, 1e-9);
    }

    [Fact]
    public void AMute_LowersASimilarItem_ButNeverBelowTheFloor()
    {
        NewsDimensions es = Dims(instruments: ["ES"]);
        // A pile of mutes drives the raw multiplier far below the floor; the clamp holds it AT the floor, above zero.
        List<SoftSignalRating> mutes = [.. Enumerable.Range(0, 20).Select(_ => Mute(es))];

        SalienceScore score = Score(mutes, es);

        score.Multiplier.Should().Be(_params.MultiplierFloor);
        score.Multiplier.Should().BeGreaterThan(0.0); // still visible -- never a hard filter (no bubble)
    }

    [Fact]
    public void AStack_OfStars_IsCapped()
    {
        NewsDimensions es = Dims(instruments: ["ES"]);
        List<SoftSignalRating> stars = [.. Enumerable.Range(0, 50).Select(_ => Star(es))];

        Score(stars, es).Multiplier.Should().Be(_params.MultiplierCap);
    }

    [Fact]
    public void UnStarring_RoundTripsToBase()
    {
        NewsDimensions es = Dims(instruments: ["ES"]);

        Score([Star(es)], es).Multiplier.Should().BeGreaterThan(1.0);
        Score([], es).Multiplier.Should().Be(1.0); // removing the star returns salience to base
    }

    [Fact]
    public void Reasons_AreRankedByAbsoluteContribution()
    {
        // Two ES stars (instrument) outweigh one fomc star (topic); ES leads the explanation.
        SoftSignalRating[] feedback =
        [
            Star(Dims(instruments: ["ES"])),
            Star(Dims(instruments: ["ES"])),
            Star(Dims(topics: ["fomc"])),
        ];

        SalienceScore score = Score(feedback, Dims(instruments: ["ES"], topics: ["fomc"]));

        score.Reasons.First().Dimension.Should().Be(SalienceDimension.Instrument);
        score.Reasons.First().Value.Should().Be("ES");
        score.Reasons.First().Contribution.Should().BeGreaterThan(score.Reasons.Last().Contribution);
    }

    [Fact]
    public void AMuteContribution_IsNegative()
    {
        SalienceScore score = Score([Mute(Dims(instruments: ["ES"]))], Dims(instruments: ["ES"]));

        score.Reasons.Should().ContainSingle().Which.Contribution.Should().BeLessThan(0.0);
        score.Multiplier.Should().BeLessThan(1.0);
    }

    [Fact]
    public void DecayDisabled_ByNonPositiveHalfLife_KeepsFullWeight()
    {
        SalienceParameters noDecay = _params with { HalfLifeDays = 0.0 };
        NewsDimensions es = Dims(instruments: ["ES"]);

        double fresh = SalienceScorer.Score(SalienceProfile.Build([Star(es, _now)], _now, noDecay), es, noDecay).Multiplier;
        double ancient = SalienceScorer.Score(SalienceProfile.Build([Star(es, _now.AddDays(-365))], _now, noDecay), es, noDecay).Multiplier;

        ancient.Should().Be(fresh); // no decay -> age is irrelevant
    }

    // ---- semantic-embedding axis (gh#853) ----

    [Fact]
    public void ASemanticNeighbor_RaisesTheMultiplier_WithASemanticReason()
    {
        // The axis rides on the item (the caller already ranked it against the operator's stars); even a cold-start
        // profile with no categorical match is raised when the item is semantically near a starred one.
        SalienceScore score = ScoreSemantic(Dims() with { SemanticSimilarity = 0.8 });

        score.Multiplier.Should().BeApproximately(1.8, 1e-9); // 1 + 0.8 * SemanticWeight(1.0)
        score.Reasons.Should().ContainSingle(reason => reason.Dimension == SalienceDimension.SemanticEmbedding);
    }

    [Fact]
    public void ANullSemanticAxis_ContributesNothing()
    {
        // No axis supplied (the unavailable / degrade path hands null): the item scores on its categorical dimensions
        // alone, with no semantic reason. SemanticSimilarity defaults to null here.
        SalienceScore score = Score([Star(Dims(instruments: ["ES"]))], Dims(instruments: ["ES"]));

        score.Reasons.Should().NotContain(reason => reason.Dimension == SalienceDimension.SemanticEmbedding);
    }

    [Fact]
    public void ANonPositiveSemanticSimilarity_ContributesNothing()
    {
        // similarity == 0 (an orthogonal / clamped-negative neighbour) is no signal: the guard is sim > 0, so it adds
        // no reason and leaves the item at base rather than pinning a zero-weight reason on it.
        SalienceScore score = ScoreSemantic(Dims() with { SemanticSimilarity = 0.0 });

        score.Multiplier.Should().Be(1.0);
        score.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void TheSemanticContribution_ScalesWithSimilarity()
    {
        double near = ScoreSemantic(Dims() with { SemanticSimilarity = 0.9 }).Multiplier;
        double far = ScoreSemantic(Dims() with { SemanticSimilarity = 0.3 }).Multiplier;

        near.Should().BeGreaterThan(far);
        near.Should().BeApproximately(1.9, 1e-9);
        far.Should().BeApproximately(1.3, 1e-9);
    }

    [Fact]
    public void TheSemanticContribution_ScalesWithSemanticWeight()
    {
        NewsDimensions item = Dims() with { SemanticSimilarity = 0.5 };

        double unit = ScoreSemantic(item).Multiplier;                                          // 1 + 0.5 * 1.0
        double heavy = ScoreSemantic(item, _params with { SemanticWeight = 3.0 }).Multiplier;  // 1 + 0.5 * 3.0

        unit.Should().BeApproximately(1.5, 1e-9);
        heavy.Should().BeApproximately(2.5, 1e-9);
    }

    [Fact]
    public void TheSemanticContribution_IsClampedAtTheCap()
    {
        // A runaway weight cannot push salience past the cap -- the semantic axis folds into the same clamp as the
        // categorical dimensions, so it stays a soft, bounded weight (never a hard boost, mirroring the star cap).
        SalienceScore score = ScoreSemantic(
            Dims() with { SemanticSimilarity = 1.0 }, _params with { SemanticWeight = 100.0 });

        score.Multiplier.Should().Be(_params.MultiplierCap);
    }

    [Fact]
    public void TheSemanticContribution_ComposesAdditivelyWithCategoricalDimensions()
    {
        NewsDimensions categorical = Dims(instruments: ["ES"]);
        NewsDimensions both = Dims(instruments: ["ES"]) with { SemanticSimilarity = 0.5 };

        SalienceScore categoricalOnly = Score([Star(categorical)], categorical);
        SalienceScore withSemantic = Score([Star(categorical)], both);

        withSemantic.Multiplier.Should().BeGreaterThan(categoricalOnly.Multiplier);
        withSemantic.Reasons.Should().Contain(reason => reason.Dimension == SalienceDimension.Instrument);
        withSemantic.Reasons.Should().Contain(reason => reason.Dimension == SalienceDimension.SemanticEmbedding);
    }
}
