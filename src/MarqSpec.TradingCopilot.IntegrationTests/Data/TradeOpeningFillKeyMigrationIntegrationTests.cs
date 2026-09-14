using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// The <b>migration</b> half of gh#759's composite trade key (gh#799 bullets 4 and 5; ADR-0022): what
/// <c>AddTradeOpeningFillKey</c> does to a <c>Trades</c> table that already holds rows, and what its <c>Down</c>
/// does once the split it enables actually exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite owns its own container</b> (the <see cref="TriggerConfirmationBackfillIntegrationTests"/>
/// pattern). Every other integration suite reaches a database already migrated to head — precisely the state in
/// which this behaviour is invisible. To witness a migration you must stand before it: migrate to the revision
/// <i>preceding</i> the one under test, write a row as the old world would have written it, and only then let the
/// migration run. xUnit builds a fresh instance of this class per test, so each case gets its own container and its
/// own unmigrated database.
/// </para>
/// <para>
/// <b>What is at stake in the Up direction.</b> The new unique index is <b>filtered on both columns being
/// non-null</b>. A pre-#759 row carries a <c>ClosingFillId</c> and no <c>OpeningFillId</c>, so it must fall OUT of
/// that index — otherwise the first new leg that happens to be retired by the same fill collides with a historical
/// row it has nothing to do with, the writer's backstop reads that collision as "already journalled", and a real
/// trade is silently dropped from the R-4 / R-5 daily governor.
/// </para>
/// <para>
/// <b>What is at stake in the Down direction.</b> <c>Down</c> re-narrows to a UNIQUE index on <c>ClosingFillId</c>
/// alone — exactly the constraint #759 removed, because it is no longer true of the data: the two legs of a
/// spanning exit legitimately share one closing fill. The same documented hazard class as the outbox-dedup
/// narrowing (<c>20260729225853</c>). Both directions are asserted on ONE database so the spanning-exit row is the
/// only difference between them: without the clean-revert control, "the hazard" would be indistinguishable from
/// "<c>Down</c> is simply broken".
/// </para>
/// <para>
/// <b>Base:</b> cut from <c>feat/759_journal-pairing-policy</c> (PR #800), where this migration lives. The writer's
/// own real-Postgres coverage is <c>TradeFifoPairingIntegrationTests</c>.
/// </para>
/// </remarks>
public class TradeOpeningFillKeyMigrationIntegrationTests : IAsyncLifetime
{
    /// <summary>The migration under test, matched by name so a re-timestamped file does not silently skip the guard.</summary>
    private const string MigrationUnderTest = "AddTradeOpeningFillKey";

    /// <summary>The composite unique index it introduces — the trade leg's natural key.</summary>
    private const string CompositeIndex = "IX_Trades_ClosingFillId_OpeningFillId";

    /// <summary>The single-column unique index it drops, and which <c>Down</c> tries to recreate.</summary>
    private const string LegacyClosingFillIndex = "IX_Trades_ClosingFillId";

    private const string Contract = "CON.F.US.MES.U26";

    private static readonly DateTimeOffset _origin = new(2026, 8, 11, 14, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder(PostgresApiFactory.DatabaseImage).Build();

    public Task InitializeAsync() => _database.StartAsync();

    async Task IAsyncLifetime.DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Migration_ShouldApplyOverPopulatedPreChangeRows_AndLeaveTheirClosingFillIdsFreeForNewLegs()
    {
        await using TradingCopilotDbContext database = CreateContext();
        IMigrator migrator = database.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration(database));

        (await ColumnCountAsync(database, "OpeningFillId")).Should().Be(
            0, "the pre-change schema has no OpeningFillId at all — if it did, this case would be migrating over "
            + "the wrong 'before' and could not witness anything");
        (await IndexCountAsync(database, LegacyClosingFillIndex)).Should().Be(
            1, "and the closing fill alone is still the unique natural key there");

        // Accounts / orders / fills are untouched by this migration, so the entity types describe them correctly at
        // either revision and EF can seed them. The Trade row cannot be seeded that way: the entity now carries a
        // column the pre-change table does not have. Raw SQL is what makes it a genuinely PRE-change row rather
        // than a post-change row that merely holds a null.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(database, owner);
        Guid historicalClosingFill = FillId(0x01);
        Guid newLegOpeningFill = FillId(0x02);
        await SeedOrderAsync(database, owner, accountId, OrderSide.Sell, "historic", historicalClosingFill, 5_010m, _origin);
        await SeedOrderAsync(database, owner, accountId, OrderSide.Buy, "new-leg", newLegOpeningFill, 5_000m, _origin.AddMinutes(1));

        Guid historicalTrade = await InsertPreChangeTradeAsync(database, owner, accountId, historicalClosingFill);

        await migrator.MigrateAsync();

        (await ColumnCountAsync(database, "OpeningFillId")).Should().Be(
            1, "AddTradeOpeningFillKey must apply over a POPULATED Trades table — an operator's journal is not "
            + "empty, and a migration that only works on an empty database is not a migration");
        (await IndexCountAsync(database, CompositeIndex)).Should().Be(1, "the composite key replaces the old one…");
        (await IndexCountAsync(database, LegacyClosingFillIndex)).Should().Be(
            0, "…and the single-column unique index is gone, or the split it exists to allow is still refused");

        (await NullOpeningFillCountAsync(database, historicalTrade)).Should().Be(
            1, "a row journalled before the column existed has no opening fill to name — backfilling one would "
            + "invent a pairing nobody ever observed, and would put a historical row INTO the filtered index");

        Func<Task> newLeg = () => AddLegAsync(database, owner, accountId, historicalClosingFill, newLegOpeningFill);

        await newLeg.Should().NotThrowAsync(
            "the filter excludes rows whose OpeningFillId is null, so a historical row can never collide with a new "
            + "leg that merely shares its closing fill — a false collision reads to the writer as 'already "
            + "journalled' and drops a real trade out of the daily governor");
    }

    [Fact]
    public async Task Down_ShouldFailOnceASpanningExitRowExists_ThoughItRevertsCleanlyWithoutOne()
    {
        await using TradingCopilotDbContext database = CreateContext();
        IMigrator migrator = database.GetInfrastructure().GetRequiredService<IMigrator>();
        string previous = PreviousMigration(database);
        await migrator.MigrateAsync();

        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(database, owner);
        Guid openingA = FillId(0x11);
        Guid openingB = FillId(0x12);
        Guid spanningExit = FillId(0x13);
        await SeedOrderAsync(database, owner, accountId, OrderSide.Buy, "entry-a", openingA, 5_000m, _origin);
        await SeedOrderAsync(database, owner, accountId, OrderSide.Buy, "entry-b", openingB, 4_990m, _origin.AddMinutes(1));
        await SeedOrderAsync(database, owner, accountId, OrderSide.Sell, "exit", spanningExit, 4_980m, _origin.AddMinutes(2));

        // The control. Without it, the failure below cannot be distinguished from "Down is simply broken", and the
        // hazard would go into the runbook as a superstition rather than as a fact about the data.
        Func<Task> revertEmpty = () => migrator.MigrateAsync(previous);
        await revertEmpty.Should().NotThrowAsync(
            "the revert itself is sound — it is the DATA that makes it unsafe, and saying which is the whole point "
            + "of recording the hazard");

        await migrator.MigrateAsync();

        await AddLegAsync(database, owner, accountId, spanningExit, openingA);
        await AddLegAsync(database, owner, accountId, spanningExit, openingB);

        Exception? failure = await Record.ExceptionAsync(() => migrator.MigrateAsync(previous));

        failure.Should().NotBeNull(
            "reverting past AddTradeOpeningFillKey recreates a UNIQUE index on ClosingFillId alone, which the two "
            + "legs of a spanning exit violate by construction — a rollback that appeared to succeed here would "
            + "have had to drop one of them, losing realized P&L the governor had already counted");

        PostgresException? postgres = FindPostgresException(failure);
        postgres.Should().NotBeNull("the refusal must come from Postgres, not from EF's own bookkeeping");
        postgres!.SqlState.Should().Be(
            PostgresErrorCodes.UniqueViolation,
            "the failure is the re-narrowing itself — 23505 on the recreated single-column index, the same "
            + "documented hazard class as the outbox-dedup narrowing (20260729225853)");
        (postgres.ConstraintName == LegacyClosingFillIndex
            || postgres.MessageText.Contains(LegacyClosingFillIndex, StringComparison.Ordinal))
            .Should().BeTrue(
                "and it must name the index being recreated, so an operator reading the failure knows which rows to "
                + "reconcile before retrying the rollback");
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

    private static Guid FillId(byte order) =>
        Guid.Parse($"0759ffff-0000-0000-0000-0000000000{order:x2}");

    private static async Task<Guid> SeedAccountAsync(TradingCopilotDbContext database, Guid owner)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        database.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = "Topstep", Type = FirmType.PropFirm });
        database.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = owner,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = "topstep-main",
        });
        await database.SaveChangesAsync();

        // Raw SQL for the same reason InsertPreChangeTradeAsync uses it: at this point the database is migrated
        // only as far as the PREVIOUS migration, so the entity and the table do NOT agree, and EF would emit an
        // INSERT naming every column the CURRENT model has. Any column added to Accounts after this migration
        // therefore broke this seed -- gh#780's `VenueReportsSimulated` was simply the first to do it
        // ("42703: column ... does not exist"). Naming the pre-change columns explicitly pins the seed to the
        // schema under test rather than to whatever the model has grown since.
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Accounts"
                ("Id", "UserId", "ConnectionId", "VenueAccountKey", "Name", "Stage", "Mode",
                 "IsActive", "CanTrade", "IsVisible", "Balance")
            VALUES
                ({accountId}, {owner}, {connectionId}, '9700', 'PRAC-50K',
                 {(int)AccountStage.Practice}, {(int)TradingMode.Practice}, true, true, true, 0)
            """);

        return accountId;
    }

    /// <summary>An executed order and its one fill — the real rows the trade's FKs point at, so a refusal here can
    /// only ever be the index and never a dangling reference.</summary>
    private static async Task SeedOrderAsync(
        TradingCopilotDbContext database,
        Guid owner,
        Guid accountId,
        OrderSide side,
        string venueOrderKey,
        Guid fillId,
        decimal price,
        DateTimeOffset at)
    {
        Guid orderId = Guid.NewGuid();

        database.Orders.Add(new Order
        {
            Id = orderId,
            UserId = owner,
            AccountId = accountId,
            Instrument = Contract,
            Side = side,
            Size = 1,
            Type = OrderType.Market,
            EntryPrice = price,
            PointValue = 5m,
            TickSize = 0.25m,
            Status = OrderStatus.Filled,
            Mode = TradingMode.Practice,
            VenueOrderKey = venueOrderKey,
            PlacedAt = at,
        });
        database.Fills.Add(new Fill
        {
            Id = fillId,
            UserId = owner,
            OrderId = orderId,
            VenueFillKey = fillId.ToString("N"),
            Price = price,
            Size = 1,
            ExecutedAt = at,
        });

        await database.SaveChangesAsync();
    }

    /// <summary>Writes a row exactly as it stood before the composite key: a closing fill and no opening one.</summary>
    private static async Task<Guid> InsertPreChangeTradeAsync(
        TradingCopilotDbContext database, Guid owner, Guid accountId, Guid closingFillId)
    {
        Guid tradeId = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Trades"
                ("Id", "UserId", "AccountId", "Instrument", "Side", "Size", "EntryPrice", "ExitPrice",
                 "RealizedPnL", "Mode", "ClosedAt", "ClosingFillId")
            VALUES
                ({tradeId}, {owner}, {accountId}, {Contract}, {(int)OrderSide.Buy}, 1, 5000, 5010,
                 50, {(int)TradingMode.Practice}, {_origin}, {closingFillId})
            """);
        return tradeId;
    }

    /// <summary>One composed leg, keyed the way the writer keys one. Written through the context because after the
    /// migration the entity and the table agree again — and because it is the INDEX, not the SQL, under test.</summary>
    private static async Task AddLegAsync(
        TradingCopilotDbContext database, Guid owner, Guid accountId, Guid closingFillId, Guid openingFillId)
    {
        database.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            AccountId = accountId,
            Instrument = Contract,
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 5_000m,
            ExitPrice = 4_980m,
            RealizedPnL = -100m,
            Mode = TradingMode.Practice,
            ClosedAt = _origin.AddMinutes(2),
            ClosingFillId = closingFillId,
            OpeningFillId = openingFillId,
        });
        await database.SaveChangesAsync();
    }

    // Aliased "Value" because that is the column name SqlQuery<T> projects a scalar from; cast to int because
    // count(*) is a bigint.
    private static Task<int> ColumnCountAsync(TradingCopilotDbContext database, string column) =>
        database.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM information_schema.columns
                WHERE table_name = 'Trades' AND column_name = {column}
                """)
            .SingleAsync();

    private static Task<int> IndexCountAsync(TradingCopilotDbContext database, string index) =>
        database.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_indexes
                WHERE tablename = 'Trades' AND indexname = {index}
                """)
            .SingleAsync();

    private static Task<int> NullOpeningFillCountAsync(TradingCopilotDbContext database, Guid tradeId) =>
        database.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM "Trades"
                WHERE "Id" = {tradeId} AND "OpeningFillId" IS NULL
                """)
            .SingleAsync();

    private static PostgresException? FindPostgresException(Exception? exception)
    {
        for (Exception? candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

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
