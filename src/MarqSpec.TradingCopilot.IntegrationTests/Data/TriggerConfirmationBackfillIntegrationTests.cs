using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// The <b>migration</b> half of the confirm-before-live gate (gh#486 ⇒ gh#470): what
/// <c>AddTriggerConfirmation</c> does to triggers that were already in service when it ran.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite owns its own container.</b> Every other integration suite reaches a database that is already
/// migrated to head — which is precisely the state in which this behaviour is invisible. To witness a backfill you
/// must stand before it: migrate to the revision <i>preceding</i> the one under test, write a row as the old world
/// would have written it, and only then let the migration run. That is what this does.
/// </para>
/// <para>
/// <b>What is actually at stake.</b> The column's <c>defaultValue: 0</c> is <see cref="TriggerConfirmation.Unconfirmed"/>
/// and is load-bearing for future inserts — a row that omits the column must be inert. But a default also fills
/// <i>existing</i> rows, and every trigger already in service was authored under the old rule where creating a
/// trigger armed it. An operator is relying on those firing. Left at the default they would all go quietly inert: a
/// schema upgrade silently disarming live alerts, which is the same class of failure as the outbox defect in gh#458
/// — the alert that cannot report is indistinguishable from the alert with nothing to report.
/// </para>
/// <para>
/// So the migration must do two <b>opposite</b> things at once, and both are asserted here: existing rows are
/// restored to <see cref="TriggerConfirmation.Confirmed"/> by an explicit <c>UPDATE</c>, while new rows still fall to
/// the fail-closed default. A single <c>defaultValue</c> cannot express that, which is exactly why the pair is worth
/// pinning.
/// </para>
/// </remarks>
public class TriggerConfirmationBackfillIntegrationTests : IAsyncLifetime
{
    /// <summary>The migration under test, matched by name so a re-timestamped file does not silently skip the guard.</summary>
    private const string MigrationUnderTest = "AddTriggerConfirmation";

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder(PostgresApiFactory.DatabaseImage).Build();

    public Task InitializeAsync() => _database.StartAsync();

    async Task IAsyncLifetime.DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Migration_ShouldRestoreExistingTriggersToConfirmed_SoAnUpgradeNeverDisarmsALiveAlert()
    {
        await using TradingCopilotDbContext database = CreateContext();
        IMigrator migrator = database.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration(database));

        // A trigger as the pre-gate world wrote it: enabled, armed, and — critically — with no notion of confirmation
        // at all, because the column does not exist yet. Written as raw SQL for that reason: the entity type cannot
        // describe this schema.
        Guid legacyTrigger = await InsertPreGateTriggerAsync(database);

        await migrator.MigrateAsync();

        int confirmation = await ReadConfirmationAsync(database, legacyTrigger);
        confirmation.Should().Be(
            (int)TriggerConfirmation.Confirmed,
            "a trigger the operator was already relying on must survive the upgrade still able to fire — a schema "
            + "change that silently disarms a live alert is worse than the gap it closes");
    }

    [Fact]
    public async Task Migration_ShouldStillLeaveANewRowUnconfirmed_BecauseTheDefaultIsFailClosed()
    {
        await using TradingCopilotDbContext database = CreateContext();
        IMigrator migrator = database.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync();

        // Inserted AFTER the migration, omitting the column entirely — the shape of any writer that does not know
        // about confirmation (a future rulebook compiler, a manual fix-up, a restored dump).
        Guid freshTrigger = await InsertPreGateTriggerAsync(database);

        int confirmation = await ReadConfirmationAsync(database, freshTrigger);
        confirmation.Should().Be(
            (int)TriggerConfirmation.Unconfirmed,
            "the backfill is a one-off rescue of existing rows, NOT a standing amnesty — anything authored afterwards "
            + "still arms nothing until the operator says so");
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

    /// <summary>Writes a trigger with only the columns that existed before the gate — no <c>Confirmation</c>.</summary>
    private static async Task<Guid> InsertPreGateTriggerAsync(TradingCopilotDbContext database)
    {
        Guid id = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Triggers"
                ("Id", "UserId", "Symbol", "Indicator", "Period", "ResolutionMinutes", "ConditionKind",
                 "Comparison", "Threshold", "Route", "Severity", "Enabled", "ArmState", "ArmCycle", "CreatedAt")
            VALUES
                ({id}, {Guid.NewGuid()}, 'ES', 'rsi', 14, 5, {(int)TriggerConditionKind.IndicatorThreshold},
                 {(int)IndicatorComparison.Above}, 70, {(int)TriggerRoute.Mechanical},
                 {(int)NotificationSeverity.Notify}, TRUE, {(int)TriggerArmState.Armed}, 0, NOW())
            """);
        return id;
    }

    private static async Task<int> ReadConfirmationAsync(TradingCopilotDbContext database, Guid triggerId) =>
        await database.Database
            // Aliased "Value" because that is the column name SqlQuery<T> projects a scalar from.
            .SqlQuery<int>($"SELECT \"Confirmation\" AS \"Value\" FROM \"Triggers\" WHERE \"Id\" = {triggerId}")
            .SingleAsync();

    private TradingCopilotDbContext CreateContext() => new(
        new DbContextOptionsBuilder<TradingCopilotDbContext>()
            .UseNpgsql(_database.GetConnectionString(), npgsql => npgsql.UseVector())
            .Options,
        new MigrationTestCurrentUser());

    /// <summary>No request, no operator — this suite only ever runs migrations and raw SQL.</summary>
    private sealed class MigrationTestCurrentUser : ICurrentUser
    {
        public Guid UserId => Guid.Empty;
    }
}
