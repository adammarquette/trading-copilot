using MarqSpec.TradingCopilot.Api.Journal;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Journal;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Journal;

/// <summary>
/// The closed-trade half of the Outcome writer (gh#909, R-9): it composes exactly one <see cref="Outcome"/> for every
/// closed <see cref="Trade"/> that lacks one, turning the trade's signed <c>RealizedPnL</c> into a resolution via
/// <see cref="OutcomeResolutionPolicy"/>. The design-defining behaviours: it maps the sign (win / loss / break-even
/// scratch), <b>refuses to guess</b> an open or result-less trade, is <b>idempotent</b> (never a second outcome for a
/// trade), stamps the owner from the trade (R-20), and sweeps <b>across owners</b> from its host's user-less scope.
/// </summary>
public class OutcomeJournalServiceTests
{
    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    // A distinct in-memory store per test (the caller passes a unique name), read/written across owners via
    // IgnoreQueryFilters — so the seeded owner on the context does not scope the sweep.
    private static TradingCopilotDbContext Context(string name) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(name).Options,
            new FixedUser(Guid.Empty));

    private static OutcomeJournalService Service(TradingCopilotDbContext database) =>
        new(database, NullLogger<OutcomeJournalService>.Instance);

    private static Trade ClosedTrade(Guid owner, decimal? realized, Guid? suggestionId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        AccountId = Guid.NewGuid(),
        SuggestionId = suggestionId,
        Instrument = "CON.F.US.MES.U26",
        Side = OrderSide.Buy,
        Size = 1,
        EntryPrice = 5000m,
        ExitPrice = 5010m,
        RealizedPnL = realized,
        Mode = TradingMode.Practice,
        ClosedAt = new DateTimeOffset(2026, 8, 15, 15, 0, 0, TimeSpan.Zero),
    };

    private static async Task<List<Outcome>> ComposeAndReadAsync(TradingCopilotDbContext database)
    {
        await Service(database).ComposeClosedTradeOutcomesAsync(CancellationToken.None);
        return await database.Outcomes.IgnoreQueryFilters().ToListAsync();
    }

    // A suggestion in a given lifecycle state (all required spine fields valid; in-memory ignores the CHECKs).
    private static Suggestion NewSuggestion(Guid owner, SuggestionState state) => new()
    {
        Origin = SuggestionOrigin.Scan,
        Id = Guid.NewGuid(),
        UserId = owner,
        AccountId = Guid.NewGuid(),
        Instrument = "ES",
        Side = OrderSide.Buy,
        Size = 1,
        EntryPrice = 5000m,
        StopPrice = 4990m,
        TargetPrice = 5020m,
        Mode = TradingMode.Practice,
        State = state,
        CreatedAt = new DateTimeOffset(2026, 8, 15, 14, 0, 0, TimeSpan.Zero),
        Rationale = "r",
        CitedFactors =
        [
            new CitedFactor
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                Kind = CitedFactorKind.Indicator,
                IsPrimary = true,
                TimeframeMinutes = 5,
                Indicator = "atr",
                Period = 14,
            },
        ],
        Confidence = 50,
        ExpiresAt = new DateTimeOffset(2026, 8, 15, 15, 0, 0, TimeSpan.Zero),
    };

    // The operator's disposition of a suggestion — Passed (declined), or Taken/Modified (a trade was taken).
    private static SuggestionDisposition NewDisposition(Guid owner, Guid suggestionId, SuggestionDispositionKind kind) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        SuggestionId = suggestionId,
        Kind = kind,
        Reasons = SuggestionPassReason.None,
        Deviations = SuggestionDeviation.None,
        CreatedAt = new DateTimeOffset(2026, 8, 15, 14, 30, 0, TimeSpan.Zero),
    };

    private static async Task<List<Outcome>> ComposeUnfilledAndReadAsync(TradingCopilotDbContext database)
    {
        await Service(database).ComposeUnfilledSuggestionOutcomesAsync(CancellationToken.None);
        return await database.Outcomes.IgnoreQueryFilters().ToListAsync();
    }

    // An order armed from a suggestion (the durable "taken" signal, whatever the disposition table says).
    private static Order NewOrder(Guid owner, Guid suggestionId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        AccountId = Guid.NewGuid(),
        SuggestionId = suggestionId,
        Instrument = "CON.F.US.MES.U26",
        Side = OrderSide.Buy,
        Size = 1,
        Type = OrderType.Market,
        Status = OrderStatus.Working,
        Mode = TradingMode.Practice,
        PlacedAt = new DateTimeOffset(2026, 8, 15, 14, 15, 0, TimeSpan.Zero),
    };

    [Theory]
    [InlineData(120.50, OutcomeResolution.Win)]
    [InlineData(-75.00, OutcomeResolution.Loss)]
    [InlineData(0.00, OutcomeResolution.NoFillScratch)] // a break-even round trip is a scratch, no net result
    public async Task ComposeClosedTradeOutcomesAsync_ShouldMapTheRealizedSign_ToAResolution(
        double realized, OutcomeResolution expected)
    {
        await using TradingCopilotDbContext database = Context(nameof(ComposeClosedTradeOutcomesAsync_ShouldMapTheRealizedSign_ToAResolution) + realized);
        Trade trade = ClosedTrade(Guid.NewGuid(), (decimal)realized);
        database.Trades.Add(trade);
        await database.SaveChangesAsync();

        List<Outcome> outcomes = await ComposeAndReadAsync(database);

        Outcome outcome = outcomes.Should().ContainSingle().Subject;
        outcome.Resolution.Should().Be(expected);
        outcome.TradeId.Should().Be(trade.Id);
        outcome.Simulated.Should().BeFalse(); // a taken, closed trade is never a simulation (gh#832 [J2] owns those)
    }

    [Fact]
    public async Task ComposeClosedTradeOutcomesAsync_ShouldStampTheOwnerAndCarryTheSuggestionLineage_FromTheTrade()
    {
        await using TradingCopilotDbContext database = Context(nameof(ComposeClosedTradeOutcomesAsync_ShouldStampTheOwnerAndCarryTheSuggestionLineage_FromTheTrade));
        Guid owner = Guid.NewGuid();
        Guid suggestion = Guid.NewGuid();
        database.Trades.Add(ClosedTrade(owner, 42m, suggestion));
        await database.SaveChangesAsync();

        Outcome outcome = (await ComposeAndReadAsync(database)).Should().ContainSingle().Subject;

        outcome.UserId.Should().Be(owner); // R-20: the filter does not stamp inserts, so it is set from the trade
        outcome.SuggestionId.Should().Be(suggestion); // the R-9 lineage is carried through
    }

    [Fact]
    public async Task ComposeClosedTradeOutcomesAsync_ShouldSkipAnOpenTrade()
    {
        await using TradingCopilotDbContext database = Context(nameof(ComposeClosedTradeOutcomesAsync_ShouldSkipAnOpenTrade));
        Trade open = ClosedTrade(Guid.NewGuid(), realized: null);
        open.ClosedAt = null; // still open -- its outcome is not yet known
        open.RealizedPnL = null;
        database.Trades.Add(open);
        await database.SaveChangesAsync();

        (await ComposeAndReadAsync(database)).Should().BeEmpty();
    }

    [Fact]
    public async Task ComposeClosedTradeOutcomesAsync_ShouldSkipAClosedTradeWithNoRealizedResult()
    {
        // Refuse-don't-guess (gh#832): a closed trade that carries no signed result is left un-outcomed rather than
        // scored a guess whose sign a report would trust.
        await using TradingCopilotDbContext database = Context(nameof(ComposeClosedTradeOutcomesAsync_ShouldSkipAClosedTradeWithNoRealizedResult));
        database.Trades.Add(ClosedTrade(Guid.NewGuid(), realized: null));
        await database.SaveChangesAsync();

        (await ComposeAndReadAsync(database)).Should().BeEmpty();
    }

    [Fact]
    public async Task ComposeClosedTradeOutcomesAsync_ShouldBeIdempotent_NotComposingASecondOutcomeForATrade()
    {
        await using TradingCopilotDbContext database = Context(nameof(ComposeClosedTradeOutcomesAsync_ShouldBeIdempotent_NotComposingASecondOutcomeForATrade));
        database.Trades.Add(ClosedTrade(Guid.NewGuid(), 10m));
        await database.SaveChangesAsync();

        await Service(database).ComposeClosedTradeOutcomesAsync(CancellationToken.None);
        await Service(database).ComposeClosedTradeOutcomesAsync(CancellationToken.None); // a second sweep must add nothing

        (await database.Outcomes.IgnoreQueryFilters().ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task ComposeClosedTradeOutcomesAsync_ShouldComposeAcrossOwners_InOneUserlessSweep()
    {
        // The host runs with no request user; the sweep must still reach every owner's closed trades (IgnoreQueryFilters)
        // and stamp each outcome from its own trade.
        await using TradingCopilotDbContext database = Context(nameof(ComposeClosedTradeOutcomesAsync_ShouldComposeAcrossOwners_InOneUserlessSweep));
        Guid alice = Guid.NewGuid();
        Guid bob = Guid.NewGuid();
        database.Trades.Add(ClosedTrade(alice, 5m));
        database.Trades.Add(ClosedTrade(bob, -5m));
        await database.SaveChangesAsync();

        List<Outcome> outcomes = await ComposeAndReadAsync(database);

        outcomes.Select(outcome => outcome.UserId).Should().BeEquivalentTo(new[] { alice, bob });
    }

    [Fact]
    public async Task ComposeClosedTradeOutcomesAsync_ShouldReturnTheCountWritten()
    {
        await using TradingCopilotDbContext database = Context(nameof(ComposeClosedTradeOutcomesAsync_ShouldReturnTheCountWritten));
        database.Trades.Add(ClosedTrade(Guid.NewGuid(), 1m));
        database.Trades.Add(ClosedTrade(Guid.NewGuid(), 2m));
        await database.SaveChangesAsync();

        int written = await Service(database).ComposeClosedTradeOutcomesAsync(CancellationToken.None);

        written.Should().Be(2);
    }

    [Fact]
    public async Task ComposeClosedTradeOutcomesAsync_ShouldNotRecomposeAnOutcome_WhenTheExistingOneIsSoftDeleted()
    {
        // A soft-deleted outcome KEEPS its row, so the anti-join (which keys on the presence of an outcome for the
        // trade, not its Deleted flag) still sees it — the writer must not recompose a fresh, un-deleted outcome and
        // resurrect it into the training set (gh#909 review; the stable removal that a hard delete of a trade-derived
        // outcome would break, which is why hard delete refuses one).
        await using TradingCopilotDbContext database =
            Context(nameof(ComposeClosedTradeOutcomesAsync_ShouldNotRecomposeAnOutcome_WhenTheExistingOneIsSoftDeleted));
        Trade trade = ClosedTrade(Guid.NewGuid(), 50m);
        database.Trades.Add(trade);
        Outcome soft = new() { Id = Guid.NewGuid(), UserId = trade.UserId, TradeId = trade.Id, Resolution = OutcomeResolution.Win };
        soft.SoftDelete();
        database.Outcomes.Add(soft);
        await database.SaveChangesAsync();

        int written = await Service(database).ComposeClosedTradeOutcomesAsync(CancellationToken.None);

        written.Should().Be(0); // nothing recomposed
        (await database.Outcomes.IgnoreQueryFilters().ToListAsync())
            .Should().ContainSingle().Which.Deleted.Should().BeTrue(); // still just the soft-deleted one
    }

    [Fact]
    public async Task ComposeClosedTradeOutcomesAsync_ShouldSkipATrade_WhenAHardDeleteSuppressedItsOutcome()
    {
        // A hard delete removes the outcome row but leaves a recomposition-suppression tombstone keyed on the trade
        // (gh#955). The closed-trade sweep must anti-join it — otherwise the next pass recomposes the very outcome the
        // operator confirmed removing. This is what makes a hard delete STICK (soft-delete keeps the row instead).
        await using TradingCopilotDbContext database =
            Context(nameof(ComposeClosedTradeOutcomesAsync_ShouldSkipATrade_WhenAHardDeleteSuppressedItsOutcome));
        Trade trade = ClosedTrade(Guid.NewGuid(), 50m);
        database.Trades.Add(trade);
        database.OutcomeSuppressions.Add(new OutcomeSuppression
        {
            Id = Guid.NewGuid(),
            UserId = trade.UserId,
            TradeId = trade.Id,
            CreatedAt = trade.ClosedAt!.Value,
        });
        await database.SaveChangesAsync();

        int written = await Service(database).ComposeClosedTradeOutcomesAsync(CancellationToken.None);

        written.Should().Be(0);
        (await database.Outcomes.IgnoreQueryFilters().ToListAsync()).Should().BeEmpty(); // suppressed — never recomposed
    }

    // --- Untaken-suggestion outcomes (gh#939): a terminal UNFILLED suggestion → Expired / NoFillScratch ---

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomesAsync_ShouldComposeExpired_ForAnExpiredVoidUnfilledSuggestion()
    {
        await using TradingCopilotDbContext database = Context(nameof(ComposeUnfilledSuggestionOutcomesAsync_ShouldComposeExpired_ForAnExpiredVoidUnfilledSuggestion));
        Suggestion suggestion = NewSuggestion(Guid.NewGuid(), SuggestionState.ExpiredVoid);
        database.Suggestions.Add(suggestion);
        await database.SaveChangesAsync();

        Outcome outcome = (await ComposeUnfilledAndReadAsync(database)).Should().ContainSingle().Subject;

        outcome.Resolution.Should().Be(OutcomeResolution.Expired);
        outcome.SuggestionId.Should().Be(suggestion.Id);
        outcome.TradeId.Should().BeNull(); // untaken — no trade
        outcome.UserId.Should().Be(suggestion.UserId); // R-20: stamped from the suggestion
        outcome.Simulated.Should().BeFalse(); // a real terminal disposition, not a simulation (gh#832 [J2])
    }

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomesAsync_ShouldComposeNoFillScratch_ForAPassedSuggestion()
    {
        await using TradingCopilotDbContext database = Context(nameof(ComposeUnfilledSuggestionOutcomesAsync_ShouldComposeNoFillScratch_ForAPassedSuggestion));
        Guid owner = Guid.NewGuid();
        Suggestion suggestion = NewSuggestion(owner, SuggestionState.Active); // passed before it could expire
        database.Suggestions.Add(suggestion);
        database.SuggestionDispositions.Add(NewDisposition(owner, suggestion.Id, SuggestionDispositionKind.Passed));
        await database.SaveChangesAsync();

        Outcome outcome = (await ComposeUnfilledAndReadAsync(database)).Should().ContainSingle().Subject;

        outcome.Resolution.Should().Be(OutcomeResolution.NoFillScratch);
        outcome.SuggestionId.Should().Be(suggestion.Id);
        outcome.TradeId.Should().BeNull();
    }

    [Theory]
    [InlineData(SuggestionDispositionKind.Taken)]
    [InlineData(SuggestionDispositionKind.Modified)]
    public async Task ComposeUnfilledSuggestionOutcomesAsync_ShouldSkipATakenSuggestion_TheClosedTradePathOwnsIt(
        SuggestionDispositionKind takenKind)
    {
        // A taken/modified suggestion produced a trade — its outcome is composed from the closed trade (with a TradeId),
        // never here. Even if the suggestion itself later reads ExpiredVoid, the untaken writer must not double-compose.
        await using TradingCopilotDbContext database = Context(nameof(ComposeUnfilledSuggestionOutcomesAsync_ShouldSkipATakenSuggestion_TheClosedTradePathOwnsIt) + takenKind);
        Guid owner = Guid.NewGuid();
        Suggestion suggestion = NewSuggestion(owner, SuggestionState.ExpiredVoid);
        database.Suggestions.Add(suggestion);
        database.SuggestionDispositions.Add(NewDisposition(owner, suggestion.Id, takenKind));
        await database.SaveChangesAsync();

        (await ComposeUnfilledAndReadAsync(database)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(SuggestionState.Active)]
    [InlineData(SuggestionState.Stale)]
    public async Task ComposeUnfilledSuggestionOutcomesAsync_ShouldSkipASuggestionThatIsNotYetTerminal(SuggestionState state)
    {
        // Active / Stale with no disposition is still live — not yet resolved, so no outcome.
        await using TradingCopilotDbContext database = Context(nameof(ComposeUnfilledSuggestionOutcomesAsync_ShouldSkipASuggestionThatIsNotYetTerminal) + state);
        database.Suggestions.Add(NewSuggestion(Guid.NewGuid(), state));
        await database.SaveChangesAsync();

        (await ComposeUnfilledAndReadAsync(database)).Should().BeEmpty();
    }

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomesAsync_ShouldPreferScratch_WhenAnExpiredSuggestionWasAlsoPassed()
    {
        // A pass is an explicit operator decline — the resolution — so it wins over the clock's expiry.
        await using TradingCopilotDbContext database = Context(nameof(ComposeUnfilledSuggestionOutcomesAsync_ShouldPreferScratch_WhenAnExpiredSuggestionWasAlsoPassed));
        Guid owner = Guid.NewGuid();
        Suggestion suggestion = NewSuggestion(owner, SuggestionState.ExpiredVoid);
        database.Suggestions.Add(suggestion);
        database.SuggestionDispositions.Add(NewDisposition(owner, suggestion.Id, SuggestionDispositionKind.Passed));
        await database.SaveChangesAsync();

        (await ComposeUnfilledAndReadAsync(database)).Should().ContainSingle()
            .Which.Resolution.Should().Be(OutcomeResolution.NoFillScratch);
    }

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomesAsync_ShouldBeIdempotent_AndSkipAnAlreadyOutcomedSuggestion()
    {
        await using TradingCopilotDbContext database = Context(nameof(ComposeUnfilledSuggestionOutcomesAsync_ShouldBeIdempotent_AndSkipAnAlreadyOutcomedSuggestion));
        database.Suggestions.Add(NewSuggestion(Guid.NewGuid(), SuggestionState.ExpiredVoid));
        await database.SaveChangesAsync();

        await Service(database).ComposeUnfilledSuggestionOutcomesAsync(CancellationToken.None);
        await Service(database).ComposeUnfilledSuggestionOutcomesAsync(CancellationToken.None); // second sweep adds nothing

        (await database.Outcomes.IgnoreQueryFilters().ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomesAsync_ShouldComposeAcrossOwners_InOneUserlessSweep()
    {
        await using TradingCopilotDbContext database = Context(nameof(ComposeUnfilledSuggestionOutcomesAsync_ShouldComposeAcrossOwners_InOneUserlessSweep));
        Guid alice = Guid.NewGuid();
        Guid bob = Guid.NewGuid();
        database.Suggestions.Add(NewSuggestion(alice, SuggestionState.ExpiredVoid));
        database.Suggestions.Add(NewSuggestion(bob, SuggestionState.ExpiredVoid));
        await database.SaveChangesAsync();

        (await ComposeUnfilledAndReadAsync(database)).Select(outcome => outcome.UserId)
            .Should().BeEquivalentTo(new[] { alice, bob });
    }

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomesAsync_ShouldSkipASuggestionAnOrderWasArmedFrom_EvenWithoutATakenDisposition()
    {
        // The disposition table is not a reliable "untaken" signal (gh#939 review): the take path writes it best-effort
        // and swallows faults, and a pass/take race can leave a taken suggestion journaled Passed. A suggestion an ORDER
        // was armed from is taken — its closed trade will own the outcome — so the untaken sweep must skip it and never
        // compose a spurious untaken row alongside the eventual trade one.
        await using TradingCopilotDbContext database =
            Context(nameof(ComposeUnfilledSuggestionOutcomesAsync_ShouldSkipASuggestionAnOrderWasArmedFrom_EvenWithoutATakenDisposition));
        Guid owner = Guid.NewGuid();
        Suggestion suggestion = NewSuggestion(owner, SuggestionState.ExpiredVoid); // looks terminal to the clock, but...
        database.Suggestions.Add(suggestion);
        database.Orders.Add(NewOrder(owner, suggestion.Id)); // ...an order was armed from it — it was taken
        await database.SaveChangesAsync();

        (await ComposeUnfilledAndReadAsync(database)).Should().BeEmpty();
    }

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomesAsync_ShouldSkipASuggestion_WhenAHardDeleteSuppressedItsOutcome()
    {
        // The untaken mirror of the trade case (gh#955): a hard delete of an untaken outcome leaves a suppression
        // tombstone keyed on the suggestion, and the unfilled sweep must anti-join it so the outcome is not recomposed.
        await using TradingCopilotDbContext database =
            Context(nameof(ComposeUnfilledSuggestionOutcomesAsync_ShouldSkipASuggestion_WhenAHardDeleteSuppressedItsOutcome));
        Suggestion suggestion = NewSuggestion(Guid.NewGuid(), SuggestionState.ExpiredVoid);
        database.Suggestions.Add(suggestion);
        database.OutcomeSuppressions.Add(new OutcomeSuppression
        {
            Id = Guid.NewGuid(),
            UserId = suggestion.UserId,
            SuggestionId = suggestion.Id,
            CreatedAt = suggestion.CreatedAt,
        });
        await database.SaveChangesAsync();

        int written = await Service(database).ComposeUnfilledSuggestionOutcomesAsync(CancellationToken.None);

        written.Should().Be(0);
        (await database.Outcomes.IgnoreQueryFilters().ToListAsync()).Should().BeEmpty(); // suppressed — never recomposed
    }

    [Fact]
    public void OutcomeTradeKeyIndex_ShouldMatchTheModelsRealIndexName_SoTheNarrowedCatchNeverSilentlyStopsMatching()
    {
        // Mirrors the TradeNaturalKeyIndex pin (gh#747). IsOutcomeTradeKeyViolation keys its idempotent-skip on the
        // literal index name -- a copy of a name EF derives by CONVENTION (the DbContext sets no HasDatabaseName) and
        // the migration emits, with nothing linking them at compile time. A rename would silently demote the catch-side
        // backstop and fire a false alarm on every concurrent replay. Pin the constant to the model's real relational
        // name -- InMemory ignores indexes, so build the model against Npgsql (offline, never connecting; UseVector so
        // the model builds, as the schema tests do).
        using TradingCopilotDbContext relational = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseNpgsql("Host=not-connected;Database=model-only", npgsql => npgsql.UseVector())
                .Options,
            new FixedUser(Guid.Empty));

        string indexName = relational.Model.FindEntityType(typeof(Outcome))!
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name).SequenceEqual([nameof(Outcome.TradeId)]))
            .GetDatabaseName()!;

        indexName.Should().Be(
            OutcomeJournalService.OutcomeTradeKeyIndex,
            "IsOutcomeTradeKeyViolation matches on this exact name; if the model's index name drifts from the "
            + "constant, the narrowed catch stops recognising the idempotent replay");
    }

    [Fact]
    public void OutcomeSuggestionKeyIndex_ShouldMatchTheModelsRealIndexName_SoTheNarrowedCatchNeverSilentlyStopsMatching()
    {
        // The gh#939 untaken-path mirror of the TradeId pin (gh#747): IsOutcomeSuggestionKeyViolation keys on this
        // literal name. Build the model against Npgsql (offline; InMemory ignores indexes) and pin the single-property
        // SuggestionId index's real relational name to the constant.
        using TradingCopilotDbContext relational = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseNpgsql("Host=not-connected;Database=model-only", npgsql => npgsql.UseVector())
                .Options,
            new FixedUser(Guid.Empty));

        string indexName = relational.Model.FindEntityType(typeof(Outcome))!
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name).SequenceEqual([nameof(Outcome.SuggestionId)]))
            .GetDatabaseName()!;

        indexName.Should().Be(
            OutcomeJournalService.OutcomeSuggestionKeyIndex,
            "IsOutcomeSuggestionKeyViolation matches on this exact name; a drift from the constant demotes the "
            + "untaken path's idempotency backstop");
    }
}
