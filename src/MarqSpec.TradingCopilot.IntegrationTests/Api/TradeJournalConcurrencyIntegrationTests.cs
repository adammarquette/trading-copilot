using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent real-Postgres coverage for the two ordering hazards in the production <c>Trade</c> writer
/// (gh#751, of gh#731; found by the gh#734 adversarial review and fixed at <c>206dde6</c>). R-8 / R-9.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shared root cause.</b> Fills that tie on <c>ExecutedAt</c> have no natural order — the candidate query
/// carries no <c>ORDER BY</c> and <c>Fill</c> has no venue ordinal. Production now imposes a deterministic total
/// order <c>(ExecutedAt, Id)</c> and a fail-closed span-guard. Both are about which row comes first, so both are
/// only meaningful against a real database: EF-InMemory returns insertion order and cannot express the ambiguity.
/// </para>
/// <para>
/// <b>Case 1's assertion is deliberately on the MECHANISM, and gh#751's suggested prove-red does not work.</b> The
/// card asks to revert the closing-fill tie-break and "confirm a second Trade row appears". It will not: reverting
/// <c>ThenByDescending</c> to <c>ThenBy</c> is still <i>deterministic</i>, so both flat deliveries pick the same
/// tied fill, write the same <c>ClosingFillId</c>, and the unique index rejects the replay exactly as before — the
/// suite would pass with and against the defect. Producing a genuine double-write needs Postgres to return tied
/// rows in different orders across two queries, which is its prerogative and not forceable from a test.
/// </para>
/// <para>
/// So case 1 asserts both halves of what <i>is</i> provable: the replay writes exactly <b>one</b> trade (the
/// outcome), and the closing fill is the <b>highest-Id</b> fill among those tied on the latest instant (the
/// mechanism the idempotency rests on). Flipping the tie-break reddens the second assertion deterministically,
/// which is the honest claim — the tie-break is load-bearing. Do not "simplify" that assertion away: without it
/// this case cannot fail on its defect.
/// </para>
/// <para>
/// <b>Case 2's prove-red is sound as written</b> — removing the span-guard yields the bogus size-2 trade, with no
/// ordering dependence.
/// </para>
/// </remarks>
public class TradeJournalConcurrencyIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private const string Contract = "CON.F.US.MES.U26";
    private const string OurCredentialKey = "topstep-main";
    private static readonly VenueId _projectx = VenueId.Parse("projectx");
    private static readonly DateTimeOffset _origin = new(2026, 8, 10, 14, 0, 0, TimeSpan.Zero);

    private readonly StubbedVenuePostgresFactory _factory;

    public TradeJournalConcurrencyIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AReplayedFlat_ShouldWriteOneTrade_AndPickTheSameTiedClosingFill()
    {
        // gh#751 case 1. A market exit sweeping the book fills in two executions at the SAME instant. The closing
        // fill is the trade's natural key behind the unique index, so if the two tied fills could be picked
        // differently on a replay the index would not backstop anything and the day's P&L would double-count into
        // DailyRealizedReader and the R-5 governor.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");

        DateTimeOffset exitAt = _origin.AddMinutes(5);
        Guid lowerTiedFill = FillId(1, 0x11);
        Guid higherTiedFill = FillId(1, 0xAA);

        await SeedOrderAsync(owner, accountId, OrderSide.Buy, [(FillId(1, 0x01), 5_000m, 2, _origin)]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell,
        [
            (lowerTiedFill, 5_010m, 1, exitAt),
            (higherTiedFill, 5_010m, 1, exitAt),
        ]);

        // At-least-once redelivery: the same flat, twice.
        await JournalAsync(FlatAt("9001", exitAt));
        await JournalAsync(FlatAt("9001", exitAt));

        List<Trade> trades = await TradesAsync(accountId);

        trades.Should().ContainSingle(
            "a replayed flat recomposes the same round trip and the unique ClosingFillId index rejects the second "
            + "write — a double-written trade double-counts the day's realized P&L into the governor");

        trades[0].Size.Should().Be(2, "both tied exit fills close the one size-2 entry");
        trades[0].ClosingFillId.Should().Be(
            higherTiedFill,
            "among fills tied on the latest instant the closing fill is chosen by the HIGHEST Id — that "
            + "deterministic tie-break is what makes the natural key reproducible across a replay, and is the "
            + "mechanism this case exists to pin (see the class remarks: asserting only 'one trade' would pass "
            + "with and against the defect)");
    }

    [Fact]
    public async Task ASameInstantReopen_ShouldRefuseToMergeTheTrips_LeavingTheJournalledOneAlone()
    {
        // gh#751 case 2. Trip 1 closes at t1 and trip 2 opens at the SAME instant. Ordered by (ExecutedAt, Id) the
        // reopen sorts BEFORE the close, so the exposure walk never sees the interior return-to-flat: running
        // exposure goes +1, +2, +1, 0 and the whole span looks like one size-2 position. Composing it would journal
        // a blended entry/exit describing neither trade, and would re-count trip 1's P&L. The span-guard refuses.
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9002");

        DateTimeOffset boundary = _origin.AddMinutes(5);

        // Trip 1 — journalled on its own flat. Its CLOSE carries the higher id...
        await SeedOrderAsync(owner, accountId, OrderSide.Buy, [(FillId(2, 0x01), 5_000m, 1, _origin)]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, [(FillId(2, 0xBB), 5_010m, 1, boundary)]);
        await JournalAsync(FlatAt("9002", boundary));
        (await TradesAsync(accountId)).Should().ContainSingle("trip 1 journals — the fixture's precondition");

        // ...and trip 2's OPEN carries a lower id at the same instant, so it sorts ahead of that close and hides
        // the boundary. This is the adverse order the guard exists for, pinned through the ids rather than hoped for.
        await SeedOrderAsync(owner, accountId, OrderSide.Buy, [(FillId(2, 0x22), 5_010m, 1, boundary)]);
        await SeedOrderAsync(owner, accountId, OrderSide.Sell, [(FillId(2, 0xCC), 5_020m, 1, _origin.AddMinutes(10))]);

        await JournalAsync(FlatAt("9002", _origin.AddMinutes(10)));

        List<Trade> trades = await TradesAsync(accountId);

        trades.Should().ContainSingle(
            "the composed span strictly contains trip 1's already-journalled close, so it is refused — writing it "
            + "would merge two trips into one blended P&L and double-count trip 1 into the daily governor");
        trades[0].Size.Should().Be(
            1, "the surviving row is trip 1, untouched — a merged trip would be size 2 with an average matching neither");
        trades[0].ExitPrice.Should().Be(5_010m, "trip 1's own exit, not the merged span's 5,020");
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    /// <summary>
    /// A stable, ordered fill id. <paramref name="order"/> is the LAST byte, which is the last field .NET compares,
    /// so it alone drives the <c>(ExecutedAt, Id)</c> tie-break under test — the ordering happens in memory after
    /// the fills are materialised, so this is .NET's <c>Guid</c> comparison, not Postgres's.
    /// </summary>
    /// <param name="block">
    /// Per-TEST id space, in the first (most significant) field. <c>Fill.Id</c> is a primary key and the fixture is
    /// one container shared by the whole class, so two cases seeding the same id collide on insert — which is how
    /// this suite first went red: each case passed alone and the second to run failed on a duplicate key.
    /// </param>
    /// <param name="order">Drives the tie-break within a case: higher sorts later.</param>
    private static Guid FillId(int block, byte order) =>
        Guid.Parse($"{block:x8}-0000-0000-0000-0000000000{order:x2}");

    private static PositionEvent FlatAt(string venueAccountKey, DateTimeOffset at) =>
        new(VenueAccountId.Create(_projectx, venueAccountKey), at,
            VenueContractId.Create(_projectx, Contract), NetQuantity: 0, new Price(5_010m));

    private async Task JournalAsync(PositionEvent flat)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradeJournalService journal = scope.ServiceProvider.GetRequiredService<TradeJournalService>();
        await journal.ProcessFlatAsync(flat, CancellationToken.None);
    }

    /// <summary>
    /// Scoped to ONE account. The fixture is a single container shared by the whole class, so an unscoped read
    /// makes each case see the other's rows and the count assertions become order-dependent — which is exactly how
    /// this suite first went red: case 1 passed alone and failed beside case 2.
    /// </summary>
    private async Task<List<Trade>> TradesAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.Trades
            .IgnoreQueryFilters()
            .Where(trade => trade.AccountId == accountId)
            .OrderBy(trade => trade.ClosedAt)
            .ToListAsync();
    }

    private async Task<Guid> SeedAccountAsync(Guid owner, string venueAccountKey)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await ExecuteDbAsync(database =>
        {
            database.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = "Topstep", Type = FirmType.PropFirm });
            database.Connections.Add(new Connection
            {
                Id = connectionId,
                UserId = owner,
                FirmId = firmId,
                Platform = "projectx",
                CredentialKey = OurCredentialKey,
            });
            database.Accounts.Add(new Account
            {
                Id = accountId,
                UserId = owner,
                ConnectionId = connectionId,
                VenueAccountKey = venueAccountKey,
                Name = "PRAC-50K",
                Stage = AccountStage.Practice,
                Mode = TradingMode.Practice,
                CanTrade = true,
                IsVisible = true,
            });
            return Task.CompletedTask;
        });

        return accountId;
    }

    /// <summary>Seeds an executed order and its fills with EXPLICIT ids — the tie-break under test reads them.</summary>
    private async Task SeedOrderAsync(
        Guid owner,
        Guid accountId,
        OrderSide side,
        (Guid Id, decimal Price, int Size, DateTimeOffset At)[] fills)
    {
        Guid orderId = Guid.NewGuid();

        await ExecuteDbAsync(database =>
        {
            database.Orders.Add(new Order
            {
                Id = orderId,
                UserId = owner,
                AccountId = accountId,
                Instrument = Contract,
                Side = side,
                Size = fills.Sum(fill => fill.Size),
                Type = OrderType.Market,
                EntryPrice = fills[0].Price,
                WorkingStopPrice = 4_990m,
                SafetyStopPrice = 4_980m,
                PointValue = 5m,
                Status = OrderStatus.Filled,
                Mode = TradingMode.Practice,
                VenueOrderKey = $"venue-{orderId:N}",
                PlacedAt = fills[0].At,
            });

            foreach ((Guid id, decimal price, int size, DateTimeOffset at) in fills)
            {
                database.Fills.Add(new Fill
                {
                    Id = id,
                    UserId = owner,
                    OrderId = orderId,
                    VenueFillKey = id.ToString("N"),
                    Price = price,
                    Size = size,
                    ExecutedAt = at,
                });
            }

            return Task.CompletedTask;
        });
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> stage)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await stage(database);
        await database.SaveChangesAsync();
    }
}
