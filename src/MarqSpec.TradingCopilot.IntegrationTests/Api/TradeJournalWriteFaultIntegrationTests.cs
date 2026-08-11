using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent QA coverage for the write-fault narrowing gh#747 puts on <c>TradeJournalService.ProcessFlatAsync</c>
/// (gh#779) — authored from the spec (gh#779, gh#747, gh#731; R-11; ADR-0013), not the implementation, which as of
/// this suite's authoring is not yet merged (open PR #781).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a container-tier suite.</b> The unit tier (<c>TradeJournalServiceTests</c>, <c>IsClosingFillUniquenessViolation</c>'s
/// own tests once #781 lands) proves the pure predicate against <b>constructed</b> <see cref="PostgresException"/>
/// instances and the catch-wiring against EF's <b>in-memory</b> provider via a <c>SaveChanges</c> interceptor — EF
/// InMemory never throws a real <see cref="PostgresException"/>, so neither the catch itself nor end-to-end
/// propagation is proven there. This suite proves both against <b>real Postgres</b> (Testcontainers,
/// <c>timescale/timescaledb-ha:pg17</c>), with the shipped migrations applied.
/// </para>
/// <para>
/// <b>RED BY DESIGN against `develop`, right now.</b> gh#747's narrowed catch is not merged as of this suite's
/// authoring — <c>develop</c> still carries the bare <c>catch (DbUpdateException)</c> that treats every write
/// fault, uniqueness or not, as a benign idempotent skip. Two of the five cases below are therefore expected to
/// FAIL on `develop` today, for the reason gh#747 exists to fix:
/// </para>
/// <list type="bullet">
/// <item><see cref="ProcessFlatAsync_ShouldPropagate_WhenARealNonUniquenessWriteFaultOccurs"/> — the fault is
/// swallowed and <c>ProcessFlatAsync</c> returns <c>false</c> instead of throwing.</item>
/// <item><see cref="AccountEventStreamHost_ShouldLogAndContinue_WhenTheJournalWriteFaultPropagates"/> — with
/// nothing to catch, <c>AccountEventStreamHost</c>'s "journalling ... failed" error log never fires.</item>
/// </list>
/// <para>
/// Both flip green automatically once PR #781 (gh#747) merges — do not implement that production change here to
/// make this suite pass (QA contract §"guard discipline", rule 3): a suite that cannot go green without a
/// production change has <b>found</b> a defect, and the red itself is gh#747's regression guard until it lands.
/// The other three cases (the idempotent-skip end-to-end proof, the raw unique-index assumption check, and the
/// isolation-level assertion) are provable today and are green against `develop` as shipped.
/// </para>
/// <para>
/// <b>Forcing the DB catch, not the in-memory pre-check.</b> The idempotent-skip case needs the real
/// <c>PostgresException</c> to be what rejects the replay, not the <c>alreadyJournalled</c> <c>AnyAsync</c>
/// pre-check that runs first. A concurrent/racing write would do it but is inherently non-deterministic; this
/// suite instead exploits R-20 (<c>TenantDbContext</c>'s per-user query filter, gh#779's own suggestion):
/// a phantom <see cref="Trade"/> row is seeded directly with a <b>different</b> <c>UserId</c> but the <b>same</b>
/// <c>ClosingFillId</c>. The pre-check — scoped to the real operator by the R-20 filter — genuinely finds
/// nothing. The unique index is <b>not</b> filtered by owner, so <c>SaveChanges</c> still collides with it —
/// deterministically, every run, no race required.
/// </para>
/// </remarks>
public class TradeJournalWriteFaultIntegrationTests : IClassFixture<TradeJournalWriteFaultPostgresFactory>
{
    private const string Contract = "ESM25";
    private const string SecondContract = "NQM25";
    private const string BlockTradeInsertConstraint = "ck_test_block_trade_insert";

    private static readonly HashSet<string> KnownOutcomes = new(StringComparer.Ordinal)
    {
        ExecutionMetrics.JournalWritten,
        ExecutionMetrics.JournalNotComposable,
        ExecutionMetrics.JournalBoundaryMergeRefused,
        ExecutionMetrics.JournalNoPointValue,
        ExecutionMetrics.JournalAlreadyJournalled,
        ExecutionMetrics.JournalDuplicateRejected,
    };

    private static VenueId Projectx { get; } = VenueId.Parse("projectx");

    private readonly TradeJournalWriteFaultPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public TradeJournalWriteFaultIntegrationTests(TradeJournalWriteFaultPostgresFactory factory)
    {
        _factory = factory;
    }

    // =============================================================================================================
    // The assumption the predicate rests on: a real filtered-unique-index violation names the INDEX, not a
    // generic constraint shape (gh#779's own "if it does not, the predicate must key on SqlState + a column/table
    // check instead" contingency). Proven at the raw DbContext level, independent of TradeJournalService.
    // =============================================================================================================

    [Fact]
    public async Task Persistence_ShouldRejectASecondClosingFill_WithARealUniqueViolation_CarryingTheIndexName()
    {
        Guid operatorId = await OperatorIdAsync();
        (Guid accountId, _) = await FreshDiscoveredAccountAsync();
        Guid closingFillId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            db.Trades.Add(NewTrade(Guid.NewGuid(), operatorId, accountId, closingFillId));
            await db.SaveChangesAsync();
        });

        await ExecuteDbAsync(async db =>
        {
            db.Trades.Add(NewTrade(Guid.NewGuid(), operatorId, accountId, closingFillId));

            Func<Task> save = () => db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.UniqueViolation
                    && (error.ConstraintName == "IX_Trades_ClosingFillId"
                        || error.MessageText.Contains("IX_Trades_ClosingFillId", StringComparison.Ordinal)),
                    "the predicate IsClosingFillUniquenessViolation keys on this constraint name — if PG/Npgsql "
                    + "ever stops reporting the INDEX's own name here, the predicate's assumption breaks silently "
                    + "unless this test is watching it (gh#779)");
        });
    }

    // =============================================================================================================
    // The isolation-level assumption, made explicit rather than implicit (gh#747 review, cited by gh#779).
    // =============================================================================================================

    [Fact]
    public async Task Database_ShouldRunUnderReadCommitted_TheIsolationLevelThePredicateIsWrittenFor()
    {
        // IsClosingFillUniquenessViolation is written for READ COMMITTED (Postgres's default, and what
        // TradeJournalService's bare SaveChangesAsync() runs under -- no explicit transaction/isolation is set
        // anywhere on this write path): a concurrent duplicate raises 23505 (unique_violation) and is skipped. If
        // the write ever moves under SERIALIZABLE or a retrying transaction, the SAME race can instead surface
        // as 40001 (serialization_failure), which the predicate treats -- BY DESIGN -- as a real fault and
        // rethrows. This assertion is what would notice that move.
        string isolation = await ReadCurrentIsolationLevelAsync();

        isolation.Should().Be(
            "read committed",
            "the writer's SaveChanges runs under Postgres's default isolation level -- the predicate's "
            + "23505-vs-40001 distinction is written for exactly this level (gh#747 review, gh#779)");
    }

    // =============================================================================================================
    // Acceptance criterion 1: the idempotent replay still skips -- via the REAL PostgresException on the DB
    // catch, not the in-memory pre-check. Provable today; green against `develop` as shipped (the bare catch
    // already treats a uniqueness violation as a skip).
    // =============================================================================================================

    [Fact]
    public async Task ProcessFlatAsync_ShouldSkipTheReplay_WhenARealDuplicateWriteMissesThePreCheckButHitsTheUniqueIndex()
    {
        Guid operatorId = await OperatorIdAsync();
        (Guid accountId, string venueKey) = await FreshDiscoveredAccountAsync();
        Guid closingFillId = await SeedRoundTripAsync(operatorId, accountId, Contract, "replay");

        // The phantom row: a DIFFERENT owner, the SAME closing fill. The R-20 filter hides it from the real
        // operator's pre-check; the unique index does not care whose row it is.
        await ExecuteDbAsync(async db =>
        {
            db.Trades.Add(NewTrade(Guid.NewGuid(), Guid.NewGuid(), accountId, closingFillId));
            await db.SaveChangesAsync();
        });

        _factory.Capture.Clear();

        bool journalled = await ProcessFlatAsync(Flat(venueKey, Contract, DateTimeOffset.UtcNow.AddMinutes(5)));

        journalled.Should().BeFalse(
            "the closing fill is already journalled -- a real writer must skip a replay, never double-count it");

        int rowsForThisClosingFill = await QueryDbAsync(db =>
            db.Trades.IgnoreQueryFilters().CountAsync(t => t.ClosingFillId == closingFillId));
        rowsForThisClosingFill.Should().Be(
            1, "the unique index rejected the real writer's insert attempt -- only the pre-seeded phantom row "
               + "survives, proving the write never landed a second time");

        _factory.Capture.For(ExecutionMetrics.JournalOutcomes).Should().ContainSingle(
            m => (string?)m.Tags.GetValueOrDefault("outcome") == ExecutionMetrics.JournalDuplicateRejected,
            "the real PostgresException on IX_Trades_ClosingFillId is what the writer's catch narrows on, and it "
            + "must still record the duplicate-rejected outcome the daily governor and R-4 throttle read");
    }

    // =============================================================================================================
    // Acceptance criterion 2 (PROVE-THE-RED): a non-uniqueness fault propagates. RED against `develop` today by
    // design (see the class remarks) -- flips green once PR #781 / gh#747 merges.
    // =============================================================================================================

    [Fact]
    public async Task ProcessFlatAsync_ShouldPropagate_WhenARealNonUniquenessWriteFaultOccurs()
    {
        // A genuine, non-idempotent write fault: a temporary real CHECK(false) NOT VALID constraint on "Trades"
        // (the same sanctioned real-Postgres-fault shape OrphanGuardEventLogIntegrationTests uses on "Events"),
        // dropped in a finally. NOT VALID means no existing row is scanned, so a shared container's prior rows
        // are untouched -- only inserts made while the constraint stands are rejected, with a REAL SqlState 23514
        // that is definitively not a uniqueness violation on IX_Trades_ClosingFillId.
        Guid operatorId = await OperatorIdAsync();
        (Guid accountId, string venueKey) = await FreshDiscoveredAccountAsync();
        await SeedRoundTripAsync(operatorId, accountId, Contract, "fault");

        await BlockTradeInsertsAsync();
        try
        {
            Func<Task> attempt = () => ProcessFlatAsync(Flat(venueKey, Contract, DateTimeOffset.UtcNow.AddMinutes(5)));

            // Before gh#747: the bare catch swallows this as a benign skip and ProcessFlatAsync returns false --
            // this assertion is the RED. After #781 merges: IsClosingFillUniquenessViolation returns false for a
            // CHECK violation, the narrowed catch's `when` guard misses, and the exception rethrows -- GREEN.
            await attempt.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>()
                .Where(error => error.SqlState == PostgresErrorCodes.CheckViolation
                    && (error.ConstraintName == BlockTradeInsertConstraint
                        || error.MessageText.Contains(BlockTradeInsertConstraint, StringComparison.Ordinal)),
                    "a genuine, non-idempotent write fault must PROPAGATE, never be swallowed as a benign "
                    + "duplicate skip -- the regression gh#747 fixes and this test locks down");

            // A new, DISTINCT outcome must be recorded -- gh#747/gh#779 name it JournalWriteFailed, but that
            // constant does not exist in production as of this suite's authoring, so this checks for
            // distinctness from the six KNOWN outcomes rather than a literal string this suite would have to
            // guess (and could then drift from whatever #781 actually ships).
            _factory.Capture.For(ExecutionMetrics.JournalOutcomes).Should().ContainSingle(
                m => !KnownOutcomes.Contains((string?)m.Tags.GetValueOrDefault("outcome") ?? string.Empty),
                "a propagating fault must record its OWN outcome, distinct from every existing one -- recording "
                + "'duplicate-rejected' (or any other known tag) for a non-duplicate fault is exactly the "
                + "swallow-everything defect gh#747 fixes");
        }
        finally
        {
            await UnblockTradeInsertsAsync();
        }
    }

    // =============================================================================================================
    // Acceptance criterion 3 (PROVE-THE-RED, companion to criterion 2): the outer host stays safe on the throw --
    // logs and continues, never crashes. RED against `develop` today for the same reason: with nothing thrown,
    // AccountEventStreamHost's catch never fires and its error log never appears.
    // =============================================================================================================

    [Fact]
    public async Task AccountEventStreamHost_ShouldLogAndContinue_WhenTheJournalWriteFaultPropagates()
    {
        // OcoExitService.ProcessExitAsync (the protection retire) runs BEFORE the journal write on every flat
        // event, unconditionally and independent of whether the journal later faults -- AccountEventStreamHost's
        // own ordering, separately proven by OcoCancelOnExitIntegrationTests. What THIS test adds is what happens
        // to the STREAM after the journal write faults: it must log the failure and keep consuming events, not
        // crash the loop -- so the operator sees the under-report rather than losing the rest of the session's
        // events silently.
        Guid operatorId = await OperatorIdAsync();
        (Guid accountId, string venueKey) = await FreshDiscoveredAccountAsync();
        await SeedRoundTripAsync(operatorId, accountId, Contract, "faulting");
        await SeedRoundTripAsync(operatorId, accountId, SecondContract, "surviving");

        AdversarialTestProjectXVenueFactory venueFactory =
            _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();
        venueFactory.MakeAccountStreamingSupported();

        TestAccountEventStream stream = _factory.Services.GetRequiredService<TestAccountEventStream>();

        await BlockTradeInsertsAsync();
        AccountEventStreamHost host = new(
            _factory.Services, _factory.Services.GetRequiredService<ILogger<AccountEventStreamHost>>());

        try
        {
            await host.StartAsync(CancellationToken.None);

            stream.Arm(Flat(venueKey, Contract, DateTimeOffset.UtcNow.AddMinutes(5)));

            (await WaitUntilAsync(
                () => Task.FromResult(_factory.Logs.Records.Any(record =>
                    record.Level == LogLevel.Error
                    && record.Message.Contains("Journalling the round trip", StringComparison.Ordinal)
                    && record.Message.Contains(Contract, StringComparison.Ordinal))))).Should().BeTrue(
                "AccountEventStreamHost must LOG the propagating write fault -- silence here means the day "
                + "under-reports with nobody able to see it (this is the RED companion to criterion 2: before "
                + "#781 nothing is thrown, so this log never appears)");

            // Now prove CONTINUES, not merely "did not crash before the test stopped it anyway": clear the fault
            // and feed a SECOND, independent round trip through the SAME running host/stream. Only a host whose
            // consume loop survived the first exception can still process it.
            await UnblockTradeInsertsAsync();
            stream.Arm(Flat(venueKey, SecondContract, DateTimeOffset.UtcNow.AddMinutes(6)));

            (await WaitUntilAsync(
                () => QueryDbAsync(db => db.Trades.IgnoreQueryFilters()
                    .AnyAsync(t => t.AccountId == accountId && t.Instrument == SecondContract)))).Should().BeTrue(
                "the SAME host must still be alive and consuming the stream after the fault -- a second, "
                + "unrelated round trip must still journal");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            await UnblockTradeInsertsAsync();
        }

        int faultingContractTrades = await QueryDbAsync(db =>
            db.Trades.IgnoreQueryFilters().CountAsync(t => t.AccountId == accountId && t.Instrument == Contract));
        faultingContractTrades.Should().Be(
            0, "the blocked insert never committed -- the error log is the only trace the faulting attempt left");
    }

    // =============================================================================================================
    // Helpers.
    // =============================================================================================================

    private async Task<bool> ProcessFlatAsync(PositionEvent exit)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<TradeJournalService>()
            .ProcessFlatAsync(exit, CancellationToken.None);
    }

    private PositionEvent Flat(string venueAccountKey, string instrument, DateTimeOffset at) =>
        new(
            VenueAccountId.Create(Projectx, venueAccountKey),
            at,
            VenueContractId.Create(Projectx, instrument),
            NetQuantity: 0,
            new Price(5_300m));

    /// <summary>
    /// Seeds a balanced 1-lot round trip (Buy entry, Sell exit) that composes cleanly under
    /// <c>TradeRoundTrip.TryCompose</c>. Returns the deterministic CLOSING fill's id -- the writer's natural key.
    /// </summary>
    private async Task<Guid> SeedRoundTripAsync(Guid operatorId, Guid accountId, string instrument, string tag)
    {
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        Guid entryOrderId = Guid.NewGuid();
        Guid exitOrderId = Guid.NewGuid();
        Guid closingFillId = Guid.NewGuid();

        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(NewOrder(entryOrderId, operatorId, accountId, instrument, OrderSide.Buy, $"entry-{tag}", t0));
            db.Orders.Add(NewOrder(exitOrderId, operatorId, accountId, instrument, OrderSide.Sell, $"exit-{tag}", t0.AddMinutes(1)));
            db.Fills.Add(NewFill(Guid.NewGuid(), operatorId, entryOrderId, 5_000m, t0.AddSeconds(10)));
            db.Fills.Add(NewFill(closingFillId, operatorId, exitOrderId, 5_010m, t0.AddSeconds(20)));
            await db.SaveChangesAsync();
        });

        return closingFillId;
    }

    private static Order NewOrder(
        Guid id, Guid userId, Guid accountId, string instrument, OrderSide side, string venueOrderKey, DateTimeOffset placedAt) => new()
    {
        Id = id,
        UserId = userId,
        AccountId = accountId,
        Instrument = instrument,
        Side = side,
        Size = 1,
        Type = OrderType.Market,
        EntryPrice = 5_000m,
        PointValue = 5m,
        TickSize = 0.25m,
        Status = OrderStatus.Filled,
        Mode = TradingMode.Practice,
        VenueOrderKey = venueOrderKey,
        PlacedAt = placedAt,
    };

    private static Fill NewFill(Guid id, Guid userId, Guid orderId, decimal price, DateTimeOffset executedAt) => new()
    {
        Id = id,
        UserId = userId,
        OrderId = orderId,
        VenueFillKey = id.ToString("N"),
        Price = price,
        Size = 1,
        ExecutedAt = executedAt,
    };

    private static Trade NewTrade(Guid id, Guid userId, Guid accountId, Guid closingFillId) => new()
    {
        Id = id,
        UserId = userId,
        AccountId = accountId,
        Instrument = Contract,
        Side = OrderSide.Buy,
        Size = 1,
        EntryPrice = 5_000m,
        ExitPrice = 5_010m,
        RealizedPnL = 50m,
        Mode = TradingMode.Practice,
        ClosedAt = DateTimeOffset.UtcNow,
        ClosingFillId = closingFillId,
    };

    private Task BlockTradeInsertsAsync() => ExecuteDbAsync(db =>
        db.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE \"Trades\" ADD CONSTRAINT {BlockTradeInsertConstraint} CHECK (false) NOT VALID"));

    private Task UnblockTradeInsertsAsync() => ExecuteDbAsync(db =>
        db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"Trades\" DROP CONSTRAINT IF EXISTS {BlockTradeInsertConstraint}"));

    private async Task<string> ReadCurrentIsolationLevelAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync();

        List<string> rows = await db.Database
            .SqlQuery<string>($"SELECT current_setting('transaction_isolation')")
            .ToListAsync();

        return rows[0];
    }

    /// <summary>Polls <paramref name="condition"/> up to <paramref name="attempts"/> times, <paramref name="delayMs"/>
    /// apart (the same polling shape <c>RetentionGapConsumerIntegrationTests</c> / <c>ContextSourceOutageIntegrationTests</c>
    /// use). Returns <see langword="false"/> on timeout rather than throwing, so a RED-BY-DESIGN case (criterion 3,
    /// pending #781) fails with a normal FluentAssertions message instead of an unhandled exception.</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int attempts = 300, int delayMs = 50)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    /// <summary>Wipes every account (cascading Orders / Fills / Trades) and this fixture's captured state, then
    /// stands up one fresh, discoverable Practice account through the real onboarding path -- isolating each test
    /// from every other in this shared-container class, including the venue-account-key REUSE that a fixed
    /// adversarial venue (gh#186) would otherwise let a stale prior test's row answer for.</summary>
    private async Task<(Guid AccountId, string VenueKey)> FreshDiscoveredAccountAsync()
    {
        await ExecuteDbAsync(db => db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync());
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>().ResetPositions();
        _factory.Services.GetRequiredService<TestAccountEventStream>().Reset();
        _factory.Capture.Clear();
        _factory.Logs.Clear();

        HttpClient client = await AuthenticatedClientAsync();
        return await SetupDiscoveredAccountAsync(client, $"Topstep-WriteFault-{Guid.NewGuid():N}");
    }

    private async Task<(Guid AccountId, string VenueKey)> SetupDiscoveredAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse tradeable = accounts.First(a => a.CanTrade);
        return (tradeable.Id, tradeable.VenueAccountKey);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private Task<Guid> OperatorIdAsync() => QueryDbAsync(async db =>
        (await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail)).Id);

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }
}
