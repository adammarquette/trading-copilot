using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.IntegrationTests.MarketData;

/// <summary>
/// Integration coverage for the <b>clean-historical bar store &amp; REST backfill</b> (gh#303 ⇒ gh#302, R-1, R-9,
/// R-20, ADR-0001) against <b>real PostgreSQL / TimescaleDB</b>. The claim under test is that this is a
/// <b>system of record</b>, not a second cache: bars are durable (no retention timer, independent of the 24h event
/// log), idempotent under overlapping re-polls (upsert keyed on the bucket, enforced by the composite primary key),
/// a venue revision wins in place, a still-forming bucket is never stored as final, the capability seam is honoured,
/// a failed poll heals on the next pass without killing the host, and — because market data is shared — the store
/// carries no tenant filter.
/// </summary>
/// <remarks>
/// The backfill is driven directly (<see cref="BarBackfillService.BackfillAsync"/>); the venue is the one sanctioned
/// double and is <b>adversarial</b> — it FEEDS seeded bars and can drop the <see cref="VenueCapability.HistoricalBars"/>
/// capability or throw, but it never decides which bars are final or how the merge resolves. Always-on hosts are
/// stripped by <see cref="OcoExitTestPostgresFactory"/> so the live <c>BarBackfillHost</c> never races the assertions.
/// Traces R-1 · R-9 · R-20 · ADR-0001; verifies gh#302.
/// </remarks>
public class BarStoreIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    private const string Instrument = "ES";
    private const int Resolution = 1;
    private const string VenueKey = "projectx";

    private readonly OcoExitTestPostgresFactory _factory;

    private static DateTimeOffset FixedNow => new(2026, 7, 27, 15, 0, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public BarStoreIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BarStore_ShouldRetainBarsBeyondTheEventLogWindow()
    {
        // The defining property: the clean-historical store is a SYSTEM OF RECORD, not a second cache, so it must NOT
        // ride the event log's 24h retention timer. Proven by construction against the live Timescale policies — the
        // bar store carries no retention job, while the event log (the positive control) does.
        int barsPolicies = await RetentionPolicyCountAsync("Bars");
        int eventsPolicies = await RetentionPolicyCountAsync("Events");

        eventsPolicies.Should().BeGreaterThan(0,
            "positive control: the event log DOES carry a 24h retention policy, so the introspection query sees policies");
        barsPolicies.Should().Be(0,
            "the clean-historical bar store must never age out on a timer — no retention policy, unlike the event log");
    }

    [Fact]
    public async Task BarStore_ShouldNotBeAffected_WhenTheEventLogRetentionDropsChunks()
    {
        await ResetAsync();
        // A bar dated well beyond the event log's 24h window: a store that expired with the log would lose it.
        await InsertBarAtAsync(FixedNow.AddDays(-30).ToUniversalTime(), close: 100m);

        // Operate the event log's retention as production would — clear its data. The two are independent tables.
        await ExecuteRawAsync("DELETE FROM \"Events\";");

        (await BarCountAsync()).Should().Be(1, "dropping the event log's data leaves the independent bar store untouched");
    }

    [Fact]
    public async Task Backfill_ShouldUpdateNotDuplicate_WhenTheSameWindowIsPolledTwice()
    {
        DateTimeOffset now = FixedNow;
        await ResetAsync();
        VenueFactory.SeedBar(now.AddMinutes(-3), 100m, 105m, 99m, 102m, 10);
        VenueFactory.SeedBar(now.AddMinutes(-2), 102m, 106m, 101m, 104m, 12);

        await BackfillAsync(now);
        await BackfillAsync(now); // the overlapping re-poll is the NORMAL case for a periodic backfill

        (await BarCountAsync()).Should().Be(2, "an overlapping re-poll updates each bucket in place — it never duplicates one");
    }

    [Fact]
    public async Task Backfill_ShouldEnforceUniquenessByConstruction_WhenDuplicateBarsAreWritten()
    {
        await ResetAsync();
        DateTimeOffset bucket = FixedNow.AddMinutes(-5).ToUniversalTime();
        await InsertBarAtAsync(bucket, close: 100m);

        // A second row for the SAME (venue, instrument, resolution, bucket) — the DB must reject it, not merely the
        // code avoid it. The composite primary key is the guard.
        Func<Task> duplicate = () => InsertBarAtAsync(bucket, close: 200m);

        await duplicate.Should().ThrowAsync<DbUpdateException>("the composite primary key rejects a duplicate bucket by construction");
        (await BarCountAsync()).Should().Be(1, "the duplicate never lands");
    }

    [Fact]
    public async Task Backfill_ShouldCorrectAReviseBar_WhenTheVenueRestatesIt()
    {
        DateTimeOffset now = FixedNow;
        await ResetAsync();
        DateTimeOffset open = now.AddMinutes(-3);
        VenueFactory.SeedBar(open, 100m, 105m, 99m, 102m, 10);
        await BackfillAsync(now);

        // The venue restates the same bucket with a corrected high/close/volume — the later value must win in place.
        VenueFactory.ClearBars();
        VenueFactory.SeedBar(open, 100m, 108m, 99m, 107m, 20);
        await BackfillAsync(now);

        List<BarRecord> bars = await BarsAsync();
        bars.Should().ContainSingle("a revision updates in place — never a second row for the same bucket");
        bars[0].Close.Should().Be(107m, "the later (revised) value wins");
        bars[0].High.Should().Be(108m);
        bars[0].Volume.Should().Be(20L);
    }

    [Fact]
    public async Task Backfill_ShouldNotStoreAPartialBar_WhenTheCurrentBucketIsStillForming()
    {
        DateTimeOffset now = FixedNow;
        await ResetAsync();
        VenueFactory.SeedBar(now.AddMinutes(-2), 100m, 105m, 99m, 102m, 10); // closed: its 1-minute bucket ended at now-1
        VenueFactory.SeedBar(now, 102m, 103m, 101m, 102m, 3);               // the CURRENT bucket — still forming

        await BackfillAsync(now);

        List<BarRecord> bars = await BarsAsync();
        bars.Should().ContainSingle("the still-forming current bucket is never stored as final — a partial bar is invisible corruption");
        bars[0].BucketStart.Should().Be(now.AddMinutes(-2).ToUniversalTime(), "only the closed bucket is stored");
    }

    [Fact]
    public async Task Backfill_ShouldRefuse_WhenTheVenueDoesNotAdvertiseHistoricalBars()
    {
        DateTimeOffset now = FixedNow;
        await ResetAsync();
        VenueFactory.SeedBar(now.AddMinutes(-2), 100m, 105m, 99m, 102m, 10);
        VenueFactory.MakeHistoricalBarsUnsupported(); // the venue refuses loudly at the capability seam (R-17)

        int written = await BackfillAsync(now);

        written.Should().Be(0, "a venue that does not advertise historical bars yields nothing to store");
        (await BarCountAsync()).Should().Be(0, "nothing is written when the capability is absent");
    }

    [Fact]
    public async Task Backfill_ShouldRetryNextPass_WhenAPollFails()
    {
        DateTimeOffset now = FixedNow;
        await ResetAsync();
        VenueFactory.SeedBar(now.AddMinutes(-2), 100m, 105m, 99m, 102m, 10);
        VenueFactory.MakeGetBarsThrow();

        int firstPass = await BackfillAsync(now);
        firstPass.Should().Be(0, "a failed poll writes nothing");
        (await BarCountAsync()).Should().Be(0, "a failed poll leaves no partially-written window that reads as complete");

        // The failure clears; the wide lookback re-fetches the same window, so the next pass heals the hole.
        VenueFactory.ResetPositions();
        VenueFactory.SeedBar(now.AddMinutes(-2), 100m, 105m, 99m, 102m, 10);

        int secondPass = await BackfillAsync(now);
        secondPass.Should().BeGreaterThan(0, "the next pass retries and stores the window");
        (await BarCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Backfill_ShouldNotKillTheHost_WhenTheVenueThrows()
    {
        DateTimeOffset now = FixedNow;
        await ResetAsync();
        VenueFactory.SeedBar(now.AddMinutes(-2), 100m, 105m, 99m, 102m, 10);
        VenueFactory.MakeGetBarsThrow();

        Func<Task> pass = async () => await BackfillAsync(now);

        await pass.Should().NotThrowAsync("a venue throw is caught per pair — the pass returns and the host survives to poll again");
    }

    [Fact]
    public async Task Bars_ShouldBeVisibleToEveryOperator_BecauseMarketDataIsShared()
    {
        await ResetAsync();
        await InsertBarAtAsync(FixedNow.AddMinutes(-5).ToUniversalTime(), close: 100m);

        // R-20 is explicit that reference & market data are global. The store carries no tenant filter, so two
        // different operators see the same bar; a filter here would hide the market from the operator trading it.
        (await BarCountForOperatorAsync(Guid.NewGuid())).Should().Be(1, "market data is global — visible to one operator…");
        (await BarCountForOperatorAsync(Guid.NewGuid())).Should().Be(1, "…and equally to another; a tenant filter here would be a bug");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task ResetAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(db => db.Bars.ExecuteDeleteAsync());
    }

    private async Task<int> BackfillAsync(DateTimeOffset now, int lookbackMinutes = 600)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        IOptions<BarBackfillOptions> options = Options.Create(new BarBackfillOptions
        {
            Instruments = [Instrument],
            ResolutionMinutes = [Resolution],
            LookbackMinutes = lookbackMinutes,
        });
        BarBackfillService service = new(db, options, NullLogger<BarBackfillService>.Instance);
        ITradingVenue venue = VenueFactory.Create(FirmConventions.None);
        return await service.BackfillAsync(venue, now, CancellationToken.None);
    }

    private async Task InsertBarAtAsync(DateTimeOffset bucketStart, decimal close)
    {
        await ExecuteDbAsync(async db =>
        {
            db.Bars.Add(new BarRecord
            {
                Venue = VenueKey,
                Instrument = Instrument,
                ResolutionMinutes = Resolution,
                BucketStart = bucketStart,
                Open = 100m,
                High = 110m,
                Low = 90m,
                Close = close,
                Volume = 10,
                RecordedAt = FixedNow,
            });
            await db.SaveChangesAsync();
        });
    }

    private Task<int> BarCountAsync() => QueryDbAsync(db => db.Bars.CountAsync());

    private Task<List<BarRecord>> BarsAsync() =>
        QueryDbAsync(db => db.Bars.OrderBy(bar => bar.BucketStart).ToListAsync());

    private Task<int> RetentionPolicyCountAsync(string hypertable) => QueryDbAsync(db =>
        db.Database.SqlQuery<int>(
                $"""SELECT COUNT(*)::int AS "Value" FROM timescaledb_information.jobs WHERE hypertable_name = {hypertable} AND proc_name = 'policy_retention'""")
            .SingleAsync());

    private async Task<int> BarCountForOperatorAsync(Guid operatorId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        DbContextOptions<TradingCopilotDbContext> options =
            scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();
        await using TradingCopilotDbContext db = new(options, new FixedUser(operatorId));
        return await db.Bars.CountAsync();
    }

    private Task ExecuteRawAsync(string sql) =>
        ExecuteDbAsync(db => db.Database.ExecuteSqlRawAsync(sql));

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }
}
