using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Data;

/// <summary>
/// The cited-factor set persists and reads back faithfully (gh#729, ADR-0026, R-4): a suggestion carries a set of
/// factors with exactly one primary (derived by the min-rule, never hand-set), the N=1 case round-trips the primary
/// factor's timeframe, and a cited <b>level</b> is a <b>snapshot copy</b> — it does not move when the mutable source
/// <see cref="PriceLevel"/> is re-scored or evicted, so the journal citation stays readable forever (R-4).
/// </summary>
public class CitedFactorPersistenceTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddYears(56);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private static TradingCopilotDbContext Context(Guid owner, string database) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(database).Options,
            new FixedUser(owner));

    // The suggestion spine a cited factor hangs from — the fields the model requires, none of them under test here.
    private static Suggestion NewSuggestion(Guid owner) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        AccountId = Guid.NewGuid(),
        Instrument = "ES",
        Side = OrderSide.Buy,
        Size = 2,
        EntryPrice = 5230.25m,
        StopPrice = 5222.0m,
        TargetPrice = 5248.5m,
        Mode = TradingMode.Practice,
        State = SuggestionState.Active,
        CreatedAt = _now,
        Rationale = "seeded",
        Confidence = 60,
        ExpiresAt = _now.AddHours(1),
    };

    // Mirrors issuance: build the factors, then let the domain rule flag the primary — IsPrimary is never hand-set.
    private static List<CitedFactor> IndicatorFactors(Guid owner, params (string Indicator, int Period, int Timeframe)[] specs)
    {
        List<CitedFactor> factors = [.. specs.Select(spec => new CitedFactor
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            Kind = CitedFactorKind.Indicator,
            TimeframeMinutes = spec.Timeframe,
            Indicator = spec.Indicator,
            Period = spec.Period,
        })];

        foreach (CitedFactorPrimary<CitedFactor> ranked in CitedFactorSet.DerivePrimary(factors, factor => factor.TimeframeMinutes))
        {
            ranked.Factor.IsPrimary = ranked.IsPrimary;
        }

        return factors;
    }

    [Fact]
    public async Task ASuggestion_PersistsItsCitedFactorSet_WithExactlyOnePrimary()
    {
        Guid owner = Guid.NewGuid();
        string database = Guid.NewGuid().ToString();
        Guid suggestionId;

        await using (TradingCopilotDbContext seed = Context(owner, database))
        {
            Suggestion suggestion = NewSuggestion(owner);
            suggestionId = suggestion.Id;
            // 60 / 15 / 5 — three aligning timeframes; the 5-minute one is the headline (gh#592 min-rule).
            foreach (CitedFactor factor in IndicatorFactors(owner, ("ema", 200, 60), ("rsi", 14, 15), ("rsi", 14, 5)))
            {
                suggestion.CitedFactors.Add(factor);
            }

            seed.Suggestions.Add(suggestion);
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext read = Context(owner, database);
        Suggestion loaded = await read.Suggestions
            .Include(suggestion => suggestion.CitedFactors)
            .SingleAsync(suggestion => suggestion.Id == suggestionId);

        loaded.CitedFactors.Should().HaveCount(3);
        loaded.CitedFactors.Count(factor => factor.IsPrimary).Should().Be(1, "exactly one primary (ADR-0026)");
        loaded.CitedFactors.Single(factor => factor.IsPrimary).TimeframeMinutes.Should().Be(5,
            "the primary is the smallest timeframe");
    }

    [Fact]
    public async Task TheN1PrimaryFactor_RoundTripsItsTimeframe_TheOldCitedResolution()
    {
        // The degenerate set-of-one every suggestion is today: one primary Indicator factor whose TimeframeMinutes
        // carries what the dropped Suggestion.CitedResolutionMinutes used to hold (the fired indicator's bar size).
        Guid owner = Guid.NewGuid();
        string database = Guid.NewGuid().ToString();
        Guid suggestionId;

        await using (TradingCopilotDbContext seed = Context(owner, database))
        {
            Suggestion suggestion = NewSuggestion(owner);
            suggestionId = suggestion.Id;
            suggestion.CitedFactors.Add(IndicatorFactors(owner, ("rsi", 14, 5)).Single());
            seed.Suggestions.Add(suggestion);
            await seed.SaveChangesAsync();
        }

        await using TradingCopilotDbContext read = Context(owner, database);
        CitedFactor primary = await read.Suggestions
            .Where(suggestion => suggestion.Id == suggestionId)
            .SelectMany(suggestion => suggestion.CitedFactors)
            .SingleAsync();

        primary.IsPrimary.Should().BeTrue("a set of one is its own primary");
        primary.Kind.Should().Be(CitedFactorKind.Indicator);
        primary.Indicator.Should().Be("rsi");
        primary.Period.Should().Be(14);
        primary.TimeframeMinutes.Should().Be(5, "the primary factor's timeframe is the old CitedResolutionMinutes");
    }

    [Fact]
    public async Task ACitedLevelSnapshot_StaysFixed_WhenTheSourcePriceLevelIsRescoredOrEvicted()
    {
        // R-4: PriceLevel is mutable (re-scored, merged, retired), so a cited level is COPIED, not linked. Mutating —
        // or deleting — the source must never disturb the citation, so it carries no FK, only a soft LevelId.
        Guid owner = Guid.NewGuid();
        string database = Guid.NewGuid().ToString();
        Guid factorId = Guid.NewGuid();
        Guid levelId = Guid.NewGuid();

        await using (TradingCopilotDbContext seed = Context(owner, database))
        {
            seed.PriceLevels.Add(new PriceLevel
            {
                Id = levelId,
                Venue = "TOPSTEPX",
                Instrument = "ES",
                TimeframeMinutes = 60,
                Top = 5310.00m,
                Bottom = 5305.00m,
                Kind = PriceLevelKind.Resistance,
                Significance = 8.0m,
                FormedAtBucket = _now,
                TouchCount = 3,
                Active = true,
                UpdatedAt = _now,
            });

            Suggestion suggestion = NewSuggestion(owner);
            suggestion.CitedFactors.Add(new CitedFactor
            {
                Id = factorId,
                UserId = owner,
                Kind = CitedFactorKind.Level,
                IsPrimary = true,
                TimeframeMinutes = 60,
                // The snapshot — a COPY of the zone as it stood at issuance, not a reference to it.
                LevelId = levelId,
                LevelVenue = "TOPSTEPX",
                LevelKind = (int)PriceLevelKind.Resistance,
                LevelTop = 5310.00m,
                LevelBottom = 5305.00m,
                LevelSignificance = 8.0m,
            });
            seed.Suggestions.Add(suggestion);
            await seed.SaveChangesAsync();
        }

        // The detector re-scores the zone and then evicts it — exactly the mutation R-4 says must not reach the journal.
        await using (TradingCopilotDbContext mutate = Context(owner, database))
        {
            PriceLevel live = await mutate.PriceLevels.SingleAsync(level => level.Id == levelId);
            live.Top = 9999.00m;
            live.Bottom = 9990.00m;
            live.Significance = 0.5m;
            live.Active = false;
            await mutate.SaveChangesAsync();

            mutate.PriceLevels.Remove(live);
            await mutate.SaveChangesAsync();
        }

        await using TradingCopilotDbContext read = Context(owner, database);
        CitedFactor snapshot = await read.CitedFactors.SingleAsync(factor => factor.Id == factorId);

        snapshot.LevelTop.Should().Be(5310.00m, "the snapshot is a copy, unmoved by the re-score");
        snapshot.LevelBottom.Should().Be(5305.00m);
        snapshot.LevelSignificance.Should().Be(8.0m);
        snapshot.LevelKind.Should().Be((int)PriceLevelKind.Resistance);
        snapshot.LevelVenue.Should().Be("TOPSTEPX");
        snapshot.LevelId.Should().Be(levelId, "a soft reference survives the source's deletion — there is no FK");
    }
}
