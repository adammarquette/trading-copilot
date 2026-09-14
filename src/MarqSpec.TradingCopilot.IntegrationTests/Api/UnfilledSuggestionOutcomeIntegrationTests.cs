using MarqSpec.TradingCopilot.Api.Journal;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Journal;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent real-Postgres coverage for <see cref="OutcomeJournalService.ComposeUnfilledSuggestionOutcomesAsync"/>
/// (gh#956, of gh#939, itself of gh#909; R-9) — written from gh#939's own text, not from the writer. This is the
/// <b>untaken</b> half of the sweep <see cref="OutcomeWriterIntegrationTests"/> already covers for closed trades:
/// a terminal suggestion that never became a position still needs a resolved outcome (Expired / NoFillScratch), and
/// the same idempotency and cross-owner concerns the trade sweep has apply here through a second, independently
/// keyed unique filtered index (<c>IX_Outcomes_SuggestionId</c>, filtered to <c>TradeId IS NULL</c>).
/// </summary>
/// <remarks>
/// Every case drives <see cref="OutcomeJournalService.ComposeUnfilledSuggestionOutcomesAsync"/> directly over
/// <c>Suggestion</c> / <c>SuggestionDisposition</c> / <c>Order</c> rows seeded straight through the
/// <see cref="TradingCopilotDbContext"/>. Uses <see cref="OutcomeTestPostgresFactory"/> (every hosted service
/// stripped, including the writer's own five-minute poll and the always-on venue/quote consumers), so the only
/// thing ever composing outcomes in these tests is the call each case makes.
/// </remarks>
public class UnfilledSuggestionOutcomeIntegrationTests : IClassFixture<OutcomeTestPostgresFactory>
{
    private readonly OutcomeTestPostgresFactory _factory;

    public UnfilledSuggestionOutcomeIntegrationTests(OutcomeTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // Composition: the two terminal-unfilled bases, and the taken suggestion that must compose nothing here.
    // =============================================================================================================

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomes_ShouldComposeExpired_ForAnExpiredVoidSuggestionNeverTaken()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId, SuggestionState.ExpiredVoid);

        int written = await ComposeAsync();

        written.Should().Be(1);
        Outcome outcome = (await OutcomesForSuggestionAsync(suggestionId)).Should().ContainSingle().Subject;
        outcome.Resolution.Should().Be(OutcomeResolution.Expired);
        outcome.TradeId.Should().BeNull("untaken — no trade");
    }

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomes_ShouldComposeNoFillScratch_ForAPassedSuggestion()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId, SuggestionState.Active);
        await SeedDispositionAsync(owner, suggestionId, SuggestionDispositionKind.Passed);

        int written = await ComposeAsync();

        written.Should().Be(1);
        Outcome outcome = (await OutcomesForSuggestionAsync(suggestionId)).Should().ContainSingle().Subject;
        outcome.Resolution.Should().Be(OutcomeResolution.NoFillScratch);
    }

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomes_ShouldPreferThePass_OverTheClocksExpiry()
    {
        // A pass is the operator's explicit decline — an actual resolution — and wins over the clock's later
        // ExpiredVoid stamp: the suggestion here carries BOTH, and only the pass's NoFillScratch may win.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId, SuggestionState.ExpiredVoid);
        await SeedDispositionAsync(owner, suggestionId, SuggestionDispositionKind.Passed);

        int written = await ComposeAsync();

        written.Should().Be(1);
        Outcome outcome = (await OutcomesForSuggestionAsync(suggestionId)).Should().ContainSingle().Subject;
        outcome.Resolution.Should().Be(
            OutcomeResolution.NoFillScratch, "an explicit pass wins over the clock's expiry, never Expired");
    }

    [Theory]
    [InlineData(SuggestionDispositionKind.Taken)]
    [InlineData(SuggestionDispositionKind.Modified)]
    public async Task ComposeUnfilledSuggestionOutcomes_ShouldComposeNothing_ForATakenSuggestion(
        SuggestionDispositionKind kind)
    {
        // A taken (or modified-take) suggestion produced a trade; its outcome comes from the closed-trade sweep,
        // with a TradeId — never from here, even though the suggestion's own clock state may still read ExpiredVoid.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId, SuggestionState.ExpiredVoid);
        await SeedTakeDispositionAsync(suggestionId, kind);

        int written = await ComposeAsync();

        written.Should().Be(0, $"a {kind} suggestion's outcome is the closed trade's job, never the untaken sweep's");
        (await OutcomesForSuggestionAsync(suggestionId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomes_ShouldComposeNothing_ForASuggestionAnOrderWasArmedFrom()
    {
        // gh#939 review: the take path's disposition write is best-effort AFTER the order is Working and swallows
        // faults, so a pass/take race can leave a TAKEN suggestion journaled Passed. Order.SuggestionId is the
        // durable armed fact, so a suggestion named by a real order is excluded WHATEVER its disposition (here:
        // none at all) or clock state says — the eventual closed-trade sweep is the sole writer of its outcome.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId, SuggestionState.ExpiredVoid);
        await SeedArmedOrderAsync(owner, accountId, suggestionId);

        int written = await ComposeAsync();

        written.Should().Be(
            0, "a suggestion an order was armed from is taken whatever its disposition row says — never a spurious untaken row");
        (await OutcomesForSuggestionAsync(suggestionId)).Should().BeEmpty();
    }

    // =============================================================================================================
    // Idempotency against the real index — the untaken mirror of OutcomeWriterIntegrationTests' trade-side case.
    // =============================================================================================================

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomes_ShouldRejectASecondOutcomeForTheSameSuggestion_ViaTheUniqueIndex()
    {
        // Deterministic TOCTOU-gap proof, mirroring OutcomeWriterIntegrationTests' trade-side case (a WhenAll race
        // is not reliable — a container-local round trip can resolve the pair sequentially through the anti-join
        // alone, with or without the index behind it): let the first compose commit normally, then attempt the
        // identical untaken row shape a racing pass would have built, and assert THAT is what the index rejects.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId, SuggestionState.ExpiredVoid);

        int firstPass = await ComposeAsync();
        firstPass.Should().Be(1, "the first pass composes the untaken outcome normally");

        await ExecuteDbAsync(async db =>
        {
            db.Outcomes.Add(new Outcome
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                TradeId = null,
                SuggestionId = suggestionId,
                Resolution = OutcomeResolution.Expired,
                Simulated = false,
            });

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>(
                "a second untaken outcome for the same suggestion must be impossible")
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.UniqueViolation
                    && (error.ConstraintName == "IX_Outcomes_SuggestionId"
                        || error.MessageText.Contains("IX_Outcomes_SuggestionId", StringComparison.Ordinal)));
        });

        (await OutcomesForSuggestionAsync(suggestionId)).Should().ContainSingle(
            "exactly one untaken outcome survives — the rejected second insert never lands");
    }

    [Fact]
    public async Task Persistence_ShouldAllowMultipleTradeDerivedOutcomes_ForTheSameSuggestion_WithoutTrippingTheFilteredIndex()
    {
        // gh#759: a taken suggestion can produce several trade legs, each its own trade-derived outcome carrying
        // the SAME SuggestionId but a DIFFERENT non-null TradeId. IX_Outcomes_SuggestionId is filtered to
        // "SuggestionId IS NOT NULL AND TradeId IS NULL", so these must coexist untouched by the untaken guard above.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId, SuggestionState.ExpiredVoid);
        Guid tradeLeg1 = await SeedClosedTradeAsync(owner, accountId, suggestionId, realizedPnL: 40m);
        Guid tradeLeg2 = await SeedClosedTradeAsync(owner, accountId, suggestionId, realizedPnL: -10m);

        await ExecuteDbAsync(async db =>
        {
            db.Outcomes.Add(new Outcome
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                TradeId = tradeLeg1,
                SuggestionId = suggestionId,
                Resolution = OutcomeResolution.Win,
                Simulated = false,
            });
            db.Outcomes.Add(new Outcome
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                TradeId = tradeLeg2,
                SuggestionId = suggestionId,
                Resolution = OutcomeResolution.Loss,
                Simulated = false,
            });

            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync(
                "each trade leg carries the SAME SuggestionId but a DIFFERENT non-null TradeId, so the filtered "
                + "(TradeId IS NULL) unique index never applies to either row");
        });

        List<Outcome> legs = await OutcomesForSuggestionAsync(suggestionId);
        legs.Should().HaveCount(2);
        legs.Select(o => o.TradeId).Should().BeEquivalentTo([tradeLeg1, tradeLeg2]);
    }

    // =============================================================================================================
    // Cross-owner sweep — mirrors OutcomeWriterIntegrationTests' trade-side case for the suggestion sweep.
    // =============================================================================================================

    [Fact]
    public async Task ComposeUnfilledSuggestionOutcomes_ShouldStampEachOutcomeWithItsOwnSuggestionsOwner_AcrossOperators()
    {
        Guid ownerA = Guid.NewGuid();
        Guid ownerB = Guid.NewGuid();
        Guid accountA = await SeedAccountAsync(ownerA);
        Guid accountB = await SeedAccountAsync(ownerB);
        Guid suggestionA = await SeedSuggestionAsync(ownerA, accountA, SuggestionState.ExpiredVoid);
        Guid suggestionB = await SeedSuggestionAsync(ownerB, accountB, SuggestionState.ExpiredVoid);

        int written = await ComposeAsync();

        written.Should().Be(2);
        Outcome outcomeA = (await OutcomesForSuggestionAsync(suggestionA)).Should().ContainSingle().Subject;
        Outcome outcomeB = (await OutcomesForSuggestionAsync(suggestionB)).Should().ContainSingle().Subject;
        outcomeA.UserId.Should().Be(ownerA, "the outcome is stamped from ITS OWN suggestion's owner, not a shared caller");
        outcomeB.UserId.Should().Be(ownerB);
    }

    // =============================================================================================================
    // Cascade FK integrity via account removal (gh#956) — the chain OutcomeDbGuardIntegrationTests proves one hop
    // at a time (Trade -> Outcome, Suggestion -> Outcome) composed end to end from the top: Account.
    // =============================================================================================================

    [Fact]
    public async Task AccountRemoval_ShouldCascadeAwayAnUntakenOutcome_WithNoOrphanOrConstraintBreach()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId, SuggestionState.ExpiredVoid);
        await ComposeAsync();
        Guid outcomeId = (await OutcomesForSuggestionAsync(suggestionId)).Should().ContainSingle().Subject.Id;

        await ExecuteDbAsync(async db =>
        {
            Account account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
            db.Accounts.Remove(account);
            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync(
                "Account -> Suggestion -> Outcome is Cascade at every hop; removing the account must never trip "
                + "CK_Outcomes_ParentPresent by stranding either FK null");
        });

        (await OutcomeExistsAsync(outcomeId)).Should().BeFalse(
            "the untaken outcome dies with the account that owned its only parent suggestion");
    }

    [Fact]
    public async Task AccountRemoval_ShouldCascadeAwayATradeDerivedOutcome_WithNoOrphanOrConstraintBreach()
    {
        // Deliberately no suggestion lineage: an outcome carrying BOTH a TradeId and a SuggestionId would also die
        // via the (separately-proven) Suggestion cascade, masking a defect in the Trade cascade specifically — this
        // suite's own prove-red pass caught exactly that vacuity when the fixture carried a linked suggestion too.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, suggestionId: null, realizedPnL: 30m);
        await ComposeClosedTradeOutcomesAsync();
        Guid outcomeId = (await OutcomesForTradeAsync(tradeId)).Should().ContainSingle().Subject.Id;

        await ExecuteDbAsync(async db =>
        {
            Account account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId);
            db.Accounts.Remove(account);
            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync(
                "Account -> Trade -> Outcome is Cascade at every hop; removing the account must never strand the "
                + "trade-derived outcome against CK_Outcomes_ParentPresent");
        });

        (await OutcomeExistsAsync(outcomeId)).Should().BeFalse(
            "the trade-derived outcome dies with the account that owned its trade");
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private async Task<int> ComposeAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        OutcomeJournalService service = scope.ServiceProvider.GetRequiredService<OutcomeJournalService>();
        return await service.ComposeUnfilledSuggestionOutcomesAsync(CancellationToken.None);
    }

    private async Task<int> ComposeClosedTradeOutcomesAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        OutcomeJournalService service = scope.ServiceProvider.GetRequiredService<OutcomeJournalService>();
        return await service.ComposeClosedTradeOutcomesAsync(CancellationToken.None);
    }

    private async Task<List<Outcome>> OutcomesForSuggestionAsync(Guid suggestionId) => await QueryDbAsync(db =>
        db.Outcomes.IgnoreQueryFilters().Where(o => o.SuggestionId == suggestionId).ToListAsync());

    private async Task<List<Outcome>> OutcomesForTradeAsync(Guid tradeId) => await QueryDbAsync(db =>
        db.Outcomes.IgnoreQueryFilters().Where(o => o.TradeId == tradeId).ToListAsync());

    private async Task<bool> OutcomeExistsAsync(Guid id) => await QueryDbAsync(db =>
        db.Outcomes.IgnoreQueryFilters().AnyAsync(o => o.Id == id));

    private async Task<Guid> SeedAccountAsync(Guid owner)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            db.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = "Topstep", Type = FirmType.PropFirm });
            db.Connections.Add(new Connection
            {
                Id = connectionId,
                UserId = owner,
                FirmId = firmId,
                Platform = "projectx",
                CredentialKey = $"key-{Guid.NewGuid():N}"[..16],
            });
            db.Accounts.Add(new Account
            {
                Id = accountId,
                UserId = owner,
                ConnectionId = connectionId,
                VenueAccountKey = $"ACC-{Guid.NewGuid():N}"[..16],
                Name = "PRAC-50K",
                Stage = AccountStage.Practice,
                Mode = TradingMode.Practice,
                CanTrade = true,
                IsVisible = true,
            });
            await db.SaveChangesAsync();
        });

        return accountId;
    }

    private async Task<Guid> SeedSuggestionAsync(Guid owner, Guid accountId, SuggestionState state)
    {
        Guid suggestionId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            db.Suggestions.Add(new Suggestion
            {
                Origin = SuggestionOrigin.Scan,
                Id = suggestionId,
                UserId = owner,
                AccountId = accountId,
                Instrument = "ESM25",
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = 5_000m,
                StopPrice = 4_990m,
                TargetPrice = 5_020m,
                Mode = TradingMode.Practice,
                State = state,
                CreatedAt = DateTimeOffset.UtcNow,
                Rationale = "gh#956 fixture",
                Confidence = 50,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        });

        return suggestionId;
    }

    /// <summary>Seeds a <see cref="SuggestionDispositionKind.Passed"/> disposition — the only kind with no take snapshot.</summary>
    private async Task SeedDispositionAsync(Guid owner, Guid suggestionId, SuggestionDispositionKind kind)
    {
        await ExecuteDbAsync(async db =>
        {
            db.SuggestionDispositions.Add(new SuggestionDisposition
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                SuggestionId = suggestionId,
                Kind = kind,
                Reasons = SuggestionPassReason.None,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
    }

    /// <summary>
    /// Seeds a <see cref="SuggestionDispositionKind.Taken"/> / <see cref="SuggestionDispositionKind.Modified"/>
    /// disposition through the real <see cref="SuggestionDisposition.ForTake"/> factory (the production take path's
    /// own builder) — the take-snapshot fields and the deviations flags it stamps are exactly what
    /// <c>CK_SuggestionDispositions_TakeSnapshot</c> and <c>CK_SuggestionDispositions_Deviations_MatchModified</c>
    /// require, and hand-building the row risks tripping either by construction rather than by the case's own intent.
    /// </summary>
    private async Task SeedTakeDispositionAsync(Guid suggestionId, SuggestionDispositionKind kind)
    {
        await ExecuteDbAsync(async db =>
        {
            Suggestion suggestion = await db.Suggestions.IgnoreQueryFilters().SingleAsync(s => s.Id == suggestionId);
            decimal submittedEntry = kind == SuggestionDispositionKind.Modified
                ? suggestion.EntryPrice + 5m // deliberately deviates, so ForTake classifies it Modified
                : suggestion.EntryPrice;     // exact match, so ForTake classifies it Taken

            SuggestionDisposition disposition = SuggestionDisposition.ForTake(
                suggestion, submittedEntry, suggestion.StopPrice, suggestion.TargetPrice, suggestion.Size, DateTimeOffset.UtcNow);
            disposition.Kind.Should().Be(kind, "the seeded deviation must produce exactly the kind this case names");

            db.SuggestionDispositions.Add(disposition);
            await db.SaveChangesAsync();
        });
    }

    private async Task SeedArmedOrderAsync(Guid owner, Guid accountId, Guid suggestionId)
    {
        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                AccountId = accountId,
                SuggestionId = suggestionId,
                Instrument = "ESM25",
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Market,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                EntryPrice = 5_000m,
                WorkingStopPrice = 4_990m,
                SafetyStopPrice = 4_985m,
                ReferencePrice = 5_000m,
                TickSize = 0.25m,
                PointValue = 5m,
                PlacedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
    }

    private async Task<Guid> SeedClosedTradeAsync(Guid owner, Guid accountId, Guid? suggestionId, decimal realizedPnL)
    {
        Guid tradeId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Trades.Add(new Trade
            {
                Id = tradeId,
                UserId = owner,
                AccountId = accountId,
                SuggestionId = suggestionId,
                Instrument = "ESM25",
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = 5_000m,
                ExitPrice = 5_000m + realizedPnL,
                RealizedPnL = realizedPnL,
                Mode = TradingMode.Practice,
                ClosedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return tradeId;
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }
}
