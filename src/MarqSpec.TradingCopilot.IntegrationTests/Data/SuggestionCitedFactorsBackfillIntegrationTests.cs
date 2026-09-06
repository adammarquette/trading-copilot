using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// The <b>migration</b> half of the multi-cited-factor model (gh#958, QA for gh#729; ADR-0026) — what
/// <c>AddSuggestionCitedFactors</c> does to suggestions that already carried the old single-signal citation
/// (<c>CitedIndicator</c> / <c>CitedPeriod</c> / <c>CitedResolutionMinutes</c>) when it ran.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite owns its own container.</b> Mirrors
/// <see cref="TriggerConfirmationBackfillIntegrationTests"/>: every other suite reaches a database already
/// migrated to head, which is exactly the state in which a backfill is invisible. To witness one you must stand
/// <i>before</i> it — migrate to the revision preceding <c>AddSuggestionCitedFactors</c>, write suggestions as the
/// old single-signal world would have, then let the migration run.
/// </para>
/// <para>
/// <b>Two things are asserted at once, because the migration does two things at once.</b> Every pre-existing
/// suggestion must end with <b>exactly one</b> primary <c>Kind = 1</c> (Indicator) factor carrying the old
/// citation values, <b>and</b> the old <c>Suggestion.Cited*</c> columns must be gone afterwards — a backfill that
/// populated the new table but left the old columns in place would silently double-author the citation, and a
/// drop that ran before the backfill would lose it outright.
/// </para>
/// <para>
/// <b>The pre-gh#542 zero case.</b> Before gh#542 added resolution tracking, <c>CitedResolutionMinutes</c>
/// defaulted to <c>0</c> on rows written under the old schema. <c>CK_SuggestionCitedFactors_Timeframe_Positive</c>
/// refuses <c>TimeframeMinutes &lt;= 0</c>, so a naive <c>SELECT ... CitedResolutionMinutes</c> backfill would
/// abort the migration on any such row still in the database. The migration's <c>GREATEST("CitedResolutionMinutes",
/// 1)</c> is the fail-safe that keeps it landing at <c>1</c> instead — asserted here by seeding exactly that
/// pre-gh#542 shape and confirming the migration does not abort and the backfilled row reads <c>1</c>, not <c>0</c>.
/// </para>
/// </remarks>
public class SuggestionCitedFactorsBackfillIntegrationTests : IAsyncLifetime
{
    /// <summary>The migration under test, matched by name so a re-timestamped file does not silently skip the guard.</summary>
    private const string MigrationUnderTest = "AddSuggestionCitedFactors";

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder(PostgresApiFactory.DatabaseImage).Build();

    public Task InitializeAsync() => _database.StartAsync();

    async Task IAsyncLifetime.DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Migration_ShouldBackfillExactlyOnePrimaryIndicatorFactor_PerPreExistingSuggestion()
    {
        await using TradingCopilotDbContext database = CreateContext();
        IMigrator migrator = database.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration(database));

        Guid accountId = await SeedPracticeAccountAsync(database);

        // A normal pre-existing suggestion — the common case the backfill exists for.
        Guid normalId = await InsertPreCitedFactorSuggestionAsync(
            database, accountId, indicator: "rsi", period: 14, resolutionMinutes: 5);

        // The pre-gh#542 shape: CitedResolutionMinutes defaulted to 0 before that increment added tracking. Without
        // the migration's GREATEST(..., 1) fail-safe this row aborts the whole migration on the positive-timeframe
        // check rather than landing safely.
        Guid preGh542Id = await InsertPreCitedFactorSuggestionAsync(
            database, accountId, indicator: "ema", period: 200, resolutionMinutes: 0);

        await migrator.MigrateAsync();

        await AssertBackfilledPrimaryAsync(database, normalId, indicator: "rsi", period: 14, expectedTimeframe: 5);
        await AssertBackfilledPrimaryAsync(database, preGh542Id, indicator: "ema", period: 200, expectedTimeframe: 1);

        (await OldCitedColumnsStillExistAsync(database)).Should().BeFalse(
            "the read model reconstructs the headline from the primary factor now -- the old columns must be gone");
    }

    [Fact]
    public async Task Migration_ShouldStillLetANewSuggestionCiteASupportingFactor_BecauseTheBackfillIsAOneOffRescue()
    {
        // The anti-vacuity control, mirroring TriggerConfirmationBackfillIntegrationTests's shape: without it, the
        // case above would also pass if the migration simply broke the table for every future insert.
        await using TradingCopilotDbContext database = CreateContext();
        IMigrator migrator = database.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync();

        Guid accountId = await SeedPracticeAccountAsync(database);
        Guid suggestionId = Guid.NewGuid();

        database.Suggestions.Add(new Suggestion
        {
            Origin = SuggestionOrigin.Scan,
            Id = suggestionId,
            UserId = Guid.NewGuid(),
            AccountId = accountId,
            Instrument = "ESM25",
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 5_000m,
            StopPrice = 4_990m,
            TargetPrice = 5_020m,
            Mode = TradingMode.Practice,
            State = SuggestionState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            Rationale = "authored after the migration",
            Confidence = 60,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CitedFactors =
            [
                new CitedFactor
                {
                    Id = Guid.NewGuid(),
                    UserId = Guid.NewGuid(),
                    Kind = CitedFactorKind.Indicator,
                    IsPrimary = true,
                    TimeframeMinutes = 15,
                    Indicator = "macd",
                    Period = 12,
                },
            ],
        });

        Func<Task> save = () => database.SaveChangesAsync();
        await save.Should().NotThrowAsync("a fresh suggestion issued after the migration must still persist normally");
    }

    /// <summary>
    /// The revision immediately before the one under test, resolved from the model rather than hardcoded — a
    /// hardcoded id would quietly stop testing the backfill the moment another migration landed between them.
    /// </summary>
    private static string PreviousMigration(TradingCopilotDbContext database)
    {
        List<string> migrations = database.Database.GetMigrations().ToList();
        int index = migrations.FindIndex(id => id.EndsWith(MigrationUnderTest, StringComparison.Ordinal));
        index.Should().BeGreaterThan(
            0, $"{MigrationUnderTest} must exist and must not be the first migration, or this suite proves nothing");
        return migrations[index - 1];
    }

    /// <summary>
    /// Stands up one Practice-mode account directly via EF -- the Firm/Connection/Account schema is untouched by
    /// this migration, so writing them through the live model at the pre-migration point is safe, unlike the
    /// Suggestions row itself.
    /// </summary>
    private static async Task<Guid> SeedPracticeAccountAsync(TradingCopilotDbContext database)
    {
        Guid userId = Guid.NewGuid();
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        database.Firms.Add(new Firm
        {
            Id = firmId,
            UserId = userId,
            Name = $"Topstep-{Guid.NewGuid():N}",
            Type = FirmType.PropFirm,
        });
        database.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = userId,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = "PROJECTX_TOPSTEP",
        });
        database.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = userId,
            ConnectionId = connectionId,
            VenueAccountKey = $"BACKFILL-{Guid.NewGuid():N}"[..16],
            Name = "backfill-fixture",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
            CanTrade = true,
            IsVisible = true,
        });
        await database.SaveChangesAsync();
        return accountId;
    }

    /// <summary>
    /// Writes a suggestion with only the columns the pre-<c>AddSuggestionCitedFactors</c> schema had -- the old
    /// <c>Cited*</c> trio instead of a <see cref="CitedFactor"/> set, which the current entity model cannot even
    /// express (the columns are gone from <see cref="Suggestion"/>), so this is deliberately raw SQL.
    /// </summary>
    private static async Task<Guid> InsertPreCitedFactorSuggestionAsync(
        TradingCopilotDbContext database, Guid accountId, string indicator, int period, int resolutionMinutes)
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = createdAt.AddHours(1);

        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Suggestions"
                ("Id", "UserId", "AccountId", "Instrument", "Side", "Size", "EntryPrice", "StopPrice", "TargetPrice",
                 "Mode", "State", "CreatedAt", "Rationale", "Confidence", "ExpiresAt",
                 "CitedIndicator", "CitedPeriod", "CitedResolutionMinutes")
            VALUES
                ({id}, {Guid.NewGuid()}, {accountId}, 'ESM25', {(int)OrderSide.Buy}, 1, 5000.0, 4990.0, 5020.0,
                 {(int)TradingMode.Practice}, {(int)SuggestionState.Active}, {createdAt}, 'pre-gh#729 seed', 50,
                 {expiresAt}, {indicator}, {period}, {resolutionMinutes})
            """);
        return id;
    }

    private static async Task AssertBackfilledPrimaryAsync(
        TradingCopilotDbContext database, Guid suggestionId, string indicator, int period, int expectedTimeframe)
    {
        List<CitedFactor> factors = await database.CitedFactors.IgnoreQueryFilters()
            .Where(factor => factor.SuggestionId == suggestionId)
            .ToListAsync();

        factors.Should().HaveCount(1, "the N=1 backfill produces exactly one factor per pre-existing suggestion");
        CitedFactor factor = factors[0];
        factor.IsPrimary.Should().BeTrue("the sole backfilled factor is the suggestion's headline");
        factor.Kind.Should().Be(CitedFactorKind.Indicator, "the old columns only ever described a fired indicator");
        factor.Indicator.Should().Be(indicator);
        factor.Period.Should().Be(period);
        factor.TimeframeMinutes.Should().Be(
            expectedTimeframe, "GREATEST(CitedResolutionMinutes, 1) is the fail-safe for the pre-gh#542 zero row");
    }

    private static async Task<bool> OldCitedColumnsStillExistAsync(TradingCopilotDbContext database) =>
        await database.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM "information_schema"."columns"
                WHERE "table_name" = 'Suggestions'
                  AND "column_name" IN ('CitedIndicator', 'CitedPeriod', 'CitedResolutionMinutes')
                """)
            .SingleAsync() > 0;

    private TradingCopilotDbContext CreateContext() => new(
        new DbContextOptionsBuilder<TradingCopilotDbContext>()
            .UseNpgsql(_database.GetConnectionString(), npgsql => npgsql.UseVector())
            .Options,
        new MigrationTestCurrentUser());

    /// <summary>No request, no operator -- this suite only ever runs migrations and raw SQL / direct EF writes.</summary>
    private sealed class MigrationTestCurrentUser : ICurrentUser
    {
        public Guid UserId => Guid.Empty;
    }
}
