using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Journal;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// The <b>database's own</b> guards on the journal <c>Outcome</c> (gh#908, of gh#832; R-9 / R-15 / R-20) against real
/// Postgres — the DB-enforced half of what gh#832 landed with unit coverage only. Written from gh#908's own text, not
/// from the model or the writer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two CHECKs an in-memory provider cannot see.</b> <c>CK_Outcomes_Resolution_NotUnknown</c> refuses a persisted
/// zero resolution; <c>CK_Outcomes_ParentPresent</c> refuses a row that resolves neither a trade nor a suggestion.
/// Each case is out of range in exactly one column and names its constraint, so a refusal cannot be mistaken for a
/// different guard firing (the <c>SuggestionDatabaseGuardIntegrationTests</c> discipline).
/// </para>
/// <para>
/// <b>The FK removal behaviors trace the model's ACTUAL configuration, not gh#908's original text.</b> gh#908 was
/// filed expecting a deleted <c>Suggestion</c> to <c>SetNull</c> its outcome's <c>SuggestionId</c> and leave the row.
/// The gh#832 review (gh#939, operator decision 2026-08-16) changed that to <c>Cascade</c> — recorded in
/// <see cref="TradingCopilotDbContext"/>'s own comment on the <c>Outcome</c> → <c>Suggestion</c> FK — because
/// <c>SetNull</c> would strand an <b>untaken</b> outcome (no <c>TradeId</c>) against <c>CK_Outcomes_ParentPresent</c>
/// the instant its only parent went null. Cascade settles that for every outcome uniformly, including a
/// <b>trade-derived</b> one that also carries the suggestion's lineage: deleting the suggestion removes it too, a
/// documented lineage-loss trade-off the DbContext comment accepts because no production path deletes a suggestion
/// directly today. This suite pins the shipped, superseding behavior rather than re-asserting a stale acceptance
/// criterion — the ADR-finding posture (a fix that only works by revising its own card's ask is not a defect to
/// re-litigate here).
/// </para>
/// </remarks>
public class OutcomeDbGuardIntegrationTests : IClassFixture<OutcomeTestPostgresFactory>
{
    private readonly OutcomeTestPostgresFactory _factory;

    public OutcomeDbGuardIntegrationTests(OutcomeTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // The CHECK constraints.
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldRejectAnUnknownResolution_ViaTheAppliedCheckConstraint()
    {
        // Unknown is the unset zero -- a defaulted or bad-cast value must never masquerade as a real resolution.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 100m);

        await ExecuteDbAsync(async db =>
        {
            Outcome bad = ValidOutcome(owner, tradeId: tradeId);
            bad.Resolution = (OutcomeResolution)0;
            db.Outcomes.Add(bad);

            await ShouldViolateTheCheckAsync(() => db.SaveChangesAsync(), "CK_Outcomes_Resolution_NotUnknown");
        });
    }

    [Fact]
    public async Task Persistence_ShouldRejectAnOutcomeWithNeitherParent_ViaTheAppliedCheckConstraint()
    {
        // An outcome resolves a trade or scores a suggestion (data-dictionary ERD) -- never a free-floating row.
        Guid owner = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            Outcome bad = ValidOutcome(owner, tradeId: null, suggestionId: null);
            db.Outcomes.Add(bad);

            await ShouldViolateTheCheckAsync(() => db.SaveChangesAsync(), "CK_Outcomes_ParentPresent");
        });
    }

    [Fact]
    public async Task Persistence_ShouldAcceptAFullyValidOutcome_SoTheGuardsAboveAreNotRefusingEverything()
    {
        // The anti-vacuity control: without it, both refusals above would also pass if `Outcomes` rejected every
        // write for some unrelated reason -- proving the table broken, not the guards working.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 100m);

        await ExecuteDbAsync(async db =>
        {
            db.Outcomes.Add(ValidOutcome(owner, tradeId: tradeId));
            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync("a fully valid outcome must still persist");
        });
    }

    // =============================================================================================================
    // FK removal behaviors -- both Cascade, per the shipped model (see class remarks for the gh#939 supersession).
    // =============================================================================================================

    [Fact]
    public async Task Persistence_DeletingTheTrade_ShouldCascadeAwayItsOutcome()
    {
        // "Dies WITH its trade" (DbContext comment): so account removal, which cascades trades, carries the outcome
        // away too and never strands a trade-only row against CK_Outcomes_ParentPresent.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 250m);
        Guid outcomeId = await SeedOutcomeAsync(ValidOutcome(owner, tradeId: tradeId));

        await ExecuteDbAsync(async db =>
        {
            Trade trade = await db.Trades.IgnoreQueryFilters().SingleAsync(t => t.Id == tradeId);
            db.Trades.Remove(trade);
            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync("the outcome's own FK is Cascade, not Restrict");
        });

        (await OutcomeExistsAsync(outcomeId)).Should().BeFalse(
            "the outcome dies with its trade -- a trade-only row cannot outlive CK_Outcomes_ParentPresent's only parent");
    }

    [Fact]
    public async Task Persistence_DeletingTheSuggestion_ShouldCascadeAwayAnUntakenOutcome()
    {
        // The orphan gh#832's own review flagged: an untaken outcome (no TradeId) that SetNull would strand against
        // CK_Outcomes_ParentPresent the instant its only parent went null. Cascade is what removes that stranding.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId);
        Guid outcomeId = await SeedOutcomeAsync(ValidOutcome(owner, tradeId: null, suggestionId: suggestionId));

        await ExecuteDbAsync(async db =>
        {
            Suggestion suggestion = await db.Suggestions.IgnoreQueryFilters().SingleAsync(s => s.Id == suggestionId);
            db.Suggestions.Remove(suggestion);
            Func<Task> save = () => db.SaveChangesAsync();
            await save.Should().NotThrowAsync("the outcome's suggestion FK is Cascade, not SetNull");
        });

        (await OutcomeExistsAsync(outcomeId)).Should().BeFalse(
            "an untaken outcome dies with its only parent rather than being stranded with both keys null");
    }

    [Fact]
    public async Task Persistence_DeletingTheSuggestion_ShouldAlsoCascadeAwayATradeDerivedOutcomeCarryingItsLineage()
    {
        // The sharper case the DbContext comment accepts as a documented trade-off: a TAKEN suggestion's outcome
        // carries BOTH ids (it has a TradeId, so SetNull alone would not have stranded it) -- but the FK is Cascade
        // unconditionally, so deleting the suggestion removes this outcome too, losing the R-9 lineage. No production
        // path deletes a suggestion directly today; this pins the schema's actual permission, not a live route.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 50m, suggestionId: suggestionId);
        Guid outcomeId = await SeedOutcomeAsync(ValidOutcome(owner, tradeId: tradeId, suggestionId: suggestionId));

        await ExecuteDbAsync(async db =>
        {
            Suggestion suggestion = await db.Suggestions.IgnoreQueryFilters().SingleAsync(s => s.Id == suggestionId);
            db.Suggestions.Remove(suggestion);
            await db.SaveChangesAsync();
        });

        (await OutcomeExistsAsync(outcomeId)).Should().BeFalse(
            "Cascade applies to every outcome carrying the deleted SuggestionId, even one that still has a TradeId");
    }

    // =============================================================================================================
    // R-20 isolation.
    // =============================================================================================================

    [Fact]
    public async Task Outcome_ShouldBeVisibleOnlyToItsOwner_UnderTheR20QueryFilter()
    {
        Guid ownerA = Guid.NewGuid();
        Guid ownerB = Guid.NewGuid();
        Guid accountA = await SeedAccountAsync(ownerA);
        Guid accountB = await SeedAccountAsync(ownerB);
        Guid tradeA = await SeedClosedTradeAsync(ownerA, accountA, realizedPnL: 10m);
        Guid tradeB = await SeedClosedTradeAsync(ownerB, accountB, realizedPnL: -10m);
        Guid outcomeA = await SeedOutcomeAsync(ValidOutcome(ownerA, tradeId: tradeA));
        Guid outcomeB = await SeedOutcomeAsync(ValidOutcome(ownerB, tradeId: tradeB));

        List<Guid> visibleToA = await VisibleOutcomeIdsAsync(ownerA);
        visibleToA.Should().Contain(outcomeA, "an operator sees their own outcome")
            .And.NotContain(outcomeB, "operator A's context must not surface operator B's outcome");

        List<Guid> visibleToB = await VisibleOutcomeIdsAsync(ownerB);
        visibleToB.Should().Contain(outcomeB)
            .And.NotContain(outcomeA, "operator B's context must not surface operator A's outcome");
    }

    // =============================================================================================================
    // The R-15 flags persist through EF with their private setters.
    // =============================================================================================================

    [Fact]
    public async Task SoftDelete_ShouldRoundTripAllThreeFlagsTrue_ThroughEf()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 75m);
        Guid outcomeId = await SeedOutcomeAsync(ValidOutcome(owner, tradeId: tradeId));

        await ExecuteDbAsync(async db =>
        {
            Outcome tracked = await db.Outcomes.IgnoreQueryFilters().SingleAsync(o => o.Id == outcomeId);
            tracked.SoftDelete();
            await db.SaveChangesAsync();
        });

        Outcome reloaded = await ReloadAsync(outcomeId);
        reloaded.Deleted.Should().BeTrue();
        reloaded.TrainingExcluded.Should().BeTrue();
        reloaded.HiddenFromUser.Should().BeTrue();
    }

    [Fact]
    public async Task SetTrainingExcluded_ShouldRoundTripAlone_LeavingTheOtherTwoFlagsUntouched()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 75m);
        Guid outcomeId = await SeedOutcomeAsync(ValidOutcome(owner, tradeId: tradeId));

        await ExecuteDbAsync(async db =>
        {
            Outcome tracked = await db.Outcomes.IgnoreQueryFilters().SingleAsync(o => o.Id == outcomeId);
            tracked.SetTrainingExcluded(true);
            await db.SaveChangesAsync();
        });

        Outcome reloaded = await ReloadAsync(outcomeId);
        reloaded.TrainingExcluded.Should().BeTrue();
        reloaded.HiddenFromUser.Should().BeFalse("training exclusion is independent of default-view visibility");
        reloaded.Deleted.Should().BeFalse("an independent toggle is not a soft-delete");
    }

    [Fact]
    public async Task SetHiddenFromUser_ShouldRoundTripAlone_LeavingTheOtherTwoFlagsUntouched()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner);
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 75m);
        Guid outcomeId = await SeedOutcomeAsync(ValidOutcome(owner, tradeId: tradeId));

        await ExecuteDbAsync(async db =>
        {
            Outcome tracked = await db.Outcomes.IgnoreQueryFilters().SingleAsync(o => o.Id == outcomeId);
            tracked.SetHiddenFromUser(true);
            await db.SaveChangesAsync();
        });

        Outcome reloaded = await ReloadAsync(outcomeId);
        reloaded.HiddenFromUser.Should().BeTrue();
        reloaded.TrainingExcluded.Should().BeFalse("default-view visibility is independent of training exclusion");
        reloaded.Deleted.Should().BeFalse();
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private static Outcome ValidOutcome(Guid owner, Guid? tradeId, Guid? suggestionId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        TradeId = tradeId,
        SuggestionId = suggestionId,
        Resolution = OutcomeResolution.Win,
        Simulated = false,
    };

    /// <summary>Asserts the named CHECK refused the write -- naming it is the point, else a case could pass on a
    /// different guard firing entirely.</summary>
    private static async Task ShouldViolateTheCheckAsync(Func<Task> save, string constraint)
    {
        await save.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                && (error.ConstraintName == constraint
                    || error.MessageText.Contains(constraint, StringComparison.Ordinal)));
    }

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

    private async Task<Guid> SeedClosedTradeAsync(
        Guid owner, Guid accountId, decimal? realizedPnL, Guid? suggestionId = null)
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
                ExitPrice = 5_000m + (realizedPnL ?? 0m),
                RealizedPnL = realizedPnL,
                Mode = TradingMode.Practice,
                ClosedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        return tradeId;
    }

    private async Task<Guid> SeedSuggestionAsync(Guid owner, Guid accountId)
    {
        Guid suggestionId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            db.Suggestions.Add(new Suggestion
            {
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
                State = SuggestionState.ExpiredVoid,
                CreatedAt = DateTimeOffset.UtcNow,
                Rationale = "gh#908 fixture",
                Confidence = 50,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        });

        return suggestionId;
    }

    private async Task<Guid> SeedOutcomeAsync(Outcome outcome)
    {
        await ExecuteDbAsync(async db =>
        {
            db.Outcomes.Add(outcome);
            await db.SaveChangesAsync();
        });
        return outcome.Id;
    }

    private async Task<bool> OutcomeExistsAsync(Guid id) => await QueryDbAsync(db =>
        db.Outcomes.IgnoreQueryFilters().AnyAsync(o => o.Id == id));

    private async Task<Outcome> ReloadAsync(Guid id) => await QueryDbAsync(db =>
        db.Outcomes.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == id));

    private async Task<List<Guid>> VisibleOutcomeIdsAsync(Guid userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        DbContextOptions<TradingCopilotDbContext> options =
            scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();
        await using TradingCopilotDbContext scoped = new(options, new FixedUser(userId));
        return await scoped.Outcomes.Select(o => o.Id).ToListAsync();
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }

    private sealed class FixedUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
    }
}
