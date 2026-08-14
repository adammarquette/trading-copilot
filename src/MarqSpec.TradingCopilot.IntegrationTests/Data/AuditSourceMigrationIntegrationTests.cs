using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// The <b>migration</b> half of gh#765 (gh#789 acceptance): what <c>AddAuditSourceForKillAndFlatten</c> does to an
/// <c>AuditRecords</c> table that already holds an operator's history — the connection-loss / order-lifecycle rows
/// that predate the kill-switch &amp; auto-flatten write sites. The migration is <b>additive</b> (a nullable
/// <c>Source</c> column plus two checks), so it must apply over those rows without loss and leave their source null.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite owns its own container</b> (the <see cref="TradeOpeningFillKeyMigrationIntegrationTests"/>
/// pattern). Every other integration suite reaches a database already migrated to head — the exact state in which a
/// migration's effect is invisible. To witness it you must stand before it: migrate to the revision <i>preceding</i>
/// the one under test, write a row as the old world wrote it (raw SQL — the entity now carries a column the old
/// table lacks), and only then let the migration run. xUnit builds a fresh instance per test, so each case gets its
/// own container and its own unmigrated database.
/// </para>
/// <para>
/// <b>What is at stake.</b> <c>CK_AuditRecords_Source_MatchesAction</c> demands a stop-plan action (1–4) leave its
/// source null. A pre-#765 row is precisely that — a <c>connection-loss</c> (action 1) with no source column at all.
/// Were the migration to backfill a source, default the column non-null, or add the check before the column, the
/// <c>ADD CONSTRAINT</c> would abort against the operator's own journal and the deploy would fail. This proves it
/// does not: the historical row survives with a null source and satisfies the new correlation check by construction.
/// </para>
/// </remarks>
public class AuditSourceMigrationIntegrationTests : IAsyncLifetime
{
    /// <summary>The migration under test, matched by name so a re-timestamped file does not silently skip the guard.</summary>
    private const string MigrationUnderTest = "AddAuditSourceForKillAndFlatten";

    private static readonly DateTimeOffset _origin = new(2026, 8, 11, 14, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder(PostgresApiFactory.DatabaseImage).Build();

    public Task InitializeAsync() => _database.StartAsync();

    async Task IAsyncLifetime.DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Migration_ShouldApplyOverPreChangeRows_LeavingTheirSourceNull_AndSurviving()
    {
        await using TradingCopilotDbContext database = CreateContext();
        IMigrator migrator = database.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration(database));

        (await ColumnCountAsync(database, "Source")).Should().Be(
            0, "the pre-change schema has no Source column at all — if it did, this case would be migrating over the "
            + "wrong 'before' and could not witness anything");

        // A row exactly as the old world wrote it: a connection-loss lifecycle entry (action 1) on a synthetic stop,
        // with NO Source column. Raw SQL, because the entity now carries a Source the pre-change table does not have —
        // writing through the context would target a column that does not yet exist.
        Guid owner = Guid.NewGuid();
        Guid historicalRow = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AuditRecords"
                ("Id", "UserId", "Action", "Placement", "SyntheticRisk", "StopPlanId", "Before", "After", "Detail", "RecordedAt")
            VALUES
                ({historicalRow}, {owner}, {(int)AuditAction.ConnectionLoss}, {(int)AuditPlacement.Synthetic},
                 true, {Guid.NewGuid()}, 'Hidden', 'Orphaned', 'venue connection lost — stop orphaned', {_origin})
            """);

        // Migrate to exactly the migration under test (not head), so a future migration that also touches
        // AuditRecords can never shift what this case witnesses.
        await migrator.MigrateAsync(MigrationUnderTestId(database));

        (await ColumnCountAsync(database, "Source")).Should().Be(
            1, "AddAuditSourceForKillAndFlatten must apply over a POPULATED AuditRecords table — an operator's audit "
            + "history is not empty, and a migration that only works on an empty database is not a migration");
        (await RowCountAsync(database, historicalRow)).Should().Be(
            1, "the historical connection-loss row survives the migration — the trail is immutable and must not be lost");
        (await NullSourceCountAsync(database, historicalRow)).Should().Be(
            1, "a row written before the column existed carries no source; the migration must leave it null (backfilling "
            + "one would invent a trigger nobody observed) — and a stop-plan action with a null source satisfies "
            + "CK_AuditRecords_Source_MatchesAction, so the ADD CONSTRAINT succeeds over the operator's own journal");
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    /// <summary>
    /// The revision immediately before the one under test, resolved from the model rather than hardcoded — a
    /// hardcoded id would quietly stop testing this migration the moment another one landed between them.
    /// </summary>
    private static string PreviousMigration(TradingCopilotDbContext database)
    {
        List<string> migrations = database.Database.GetMigrations().ToList();
        int index = migrations.FindIndex(id => id.EndsWith(MigrationUnderTest, StringComparison.Ordinal));
        index.Should().BeGreaterThan(
            0, $"{MigrationUnderTest} must exist and must not be the first migration, or this suite proves nothing");
        return migrations[index - 1];
    }

    /// <summary>The migration under test itself, resolved by name — the target to migrate up to (not head).</summary>
    private static string MigrationUnderTestId(TradingCopilotDbContext database)
    {
        string? id = database.Database.GetMigrations()
            .FirstOrDefault(migration => migration.EndsWith(MigrationUnderTest, StringComparison.Ordinal));
        id.Should().NotBeNull($"{MigrationUnderTest} must exist in the model's migration list");
        return id!;
    }

    // Aliased "Value" because that is the column name SqlQuery<T> projects a scalar from; cast to int because
    // count(*) is a bigint.
    private static Task<int> ColumnCountAsync(TradingCopilotDbContext database, string column) =>
        database.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM information_schema.columns
                WHERE table_name = 'AuditRecords' AND column_name = {column}
                """)
            .SingleAsync();

    private static Task<int> RowCountAsync(TradingCopilotDbContext database, Guid id) =>
        database.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM "AuditRecords" WHERE "Id" = {id}
                """)
            .SingleAsync();

    private static Task<int> NullSourceCountAsync(TradingCopilotDbContext database, Guid id) =>
        database.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM "AuditRecords" WHERE "Id" = {id} AND "Source" IS NULL
                """)
            .SingleAsync();

    private TradingCopilotDbContext CreateContext() => new(
        new DbContextOptionsBuilder<TradingCopilotDbContext>()
            .UseNpgsql(_database.GetConnectionString(), npgsql => npgsql.UseVector())
            .Options,
        new MigrationTestCurrentUser());

    /// <summary>No request, no operator — this suite runs migrations, raw SQL, and writes of its own rows only.</summary>
    private sealed class MigrationTestCurrentUser : ICurrentUser
    {
        public Guid UserId => Guid.Empty;
    }
}
