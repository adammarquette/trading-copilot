using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Data;

/// <summary>
/// Today's realized P&amp;L reader (gh#587) — the consumed input the daily-headroom read projects over. The behaviours
/// that matter: it sums <b>closed</b> trades over the <b>Central</b> trading day (not UTC, so a CME session past UTC
/// midnight is not split), excludes open / unrealized rows, reads <b>zero not absence</b> on a quiet day, and is
/// scoped to the caller (R-20) and the account.
/// </summary>
public class DailyRealizedReaderTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    // 2026-08-03 18:00 UTC = 13:00 CDT (UTC-5 in summer), so the Central trading day is 2026-08-03; its midnight is
    // 05:00 UTC. Trades are placed either side of that boundary to pin the rollover.
    private static readonly DateTimeOffset _now = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private async Task SeedTradeAsync(
        decimal? realizedPnL, DateTimeOffset? closedAt, Guid? account = null, Guid? owner = null,
        TradingMode mode = TradingMode.Practice)
    {
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            AccountId = account ?? _account,
            Instrument = "CON.F.US.ES.U26",
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 5_300m,
            ExitPrice = closedAt is null ? null : 5_305m,
            RealizedPnL = realizedPnL,
            Mode = mode,
            ClosedAt = closedAt,
        });
        await context.SaveChangesAsync();
    }

    private async Task<decimal> ReadAsync(Guid? asUser = null, TradingMode mode = TradingMode.Practice)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await context.TodayRealizedPnLForAccountAsync(_account, mode, _now, CancellationToken.None);
    }

    [Fact]
    public async Task TodayRealizedPnL_ShouldSumTodaysClosedTrades()
    {
        await SeedTradeAsync(realizedPnL: 100m, closedAt: _now.AddHours(-2)); // 16:00 UTC, Central 08-03
        await SeedTradeAsync(realizedPnL: -30m, closedAt: _now.AddHours(-1));

        (await ReadAsync()).Should().Be(70m);
    }

    [Fact]
    public async Task TodayRealizedPnL_ShouldReturnZero_WhenNothingClosedToday()
    {
        // A quiet day is full headroom, so the honest answer is 0 consumed -- never an absence.
        (await ReadAsync()).Should().Be(0m);
    }

    [Fact]
    public async Task TodayRealizedPnL_ShouldThrow_WhenTheModeIsUndeclared()
    {
        // gh#746 review. Trade.Mode is check-constrained never to be Undeclared, so filtering by it matches zero rows
        // for any account -- a SILENT "no loss" that would report full headroom on an account that really lost money.
        // No caller should pass Undeclared; the reader refuses it loudly rather than return that accidental 0. A real
        // Live loss is on the books to make the point: were this to silently return 0, that loss would be hidden.
        await SeedTradeAsync(realizedPnL: -400m, closedAt: _now.AddHours(-1), mode: TradingMode.Live);

        Func<Task> act = () => ReadAsync(mode: TradingMode.Undeclared);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task TodayRealizedPnL_ShouldExcludeAPriorTradingDay_OnTheCentralBoundary()
    {
        // Central midnight for 08-03 is 05:00 UTC. A trade at 04:00 UTC on 08-03 is still the PREVIOUS Central day
        // (08-02) and must NOT count; one at 06:00 UTC is the new day (08-03) and must. A UTC-date reader would
        // wrongly bucket both into 08-03.
        await SeedTradeAsync(realizedPnL: -500m, closedAt: new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero));
        await SeedTradeAsync(realizedPnL: -40m, closedAt: new DateTimeOffset(2026, 8, 3, 6, 0, 0, TimeSpan.Zero));

        (await ReadAsync()).Should().Be(-40m, "only the trade after Central midnight is today's");
    }

    [Fact]
    public async Task TodayRealizedPnL_ShouldExcludeOpenAndUnrealizedTrades()
    {
        await SeedTradeAsync(realizedPnL: null, closedAt: null);                 // still open
        await SeedTradeAsync(realizedPnL: null, closedAt: _now.AddHours(-1));    // closed but no realized figure yet
        await SeedTradeAsync(realizedPnL: 25m, closedAt: _now.AddHours(-1));     // the only countable one

        (await ReadAsync()).Should().Be(25m);
    }

    [Fact]
    public async Task TodayRealizedPnL_ShouldOnlyCountTheGivenAccount()
    {
        await SeedTradeAsync(realizedPnL: 100m, closedAt: _now.AddHours(-1), account: _account);
        await SeedTradeAsync(realizedPnL: -999m, closedAt: _now.AddHours(-1), account: Guid.NewGuid());

        (await ReadAsync()).Should().Be(100m, "another account's trade is not this account's day");
    }

    [Fact]
    public async Task TodayRealizedPnL_ShouldOnlyCountTheCallersTrades()
    {
        // R-20: the reader is a request path, so the query filter scopes to the caller. Another operator's trade on
        // the same account id is invisible.
        await SeedTradeAsync(realizedPnL: -777m, closedAt: _now.AddHours(-1), owner: _other);

        (await ReadAsync(asUser: _operator)).Should().Be(0m);
    }

    [Fact]
    public async Task TodayRealizedPnL_ShouldCountOnlyTheRequestedMode_WhenAnAccountChangedModes()
    {
        // R-14 (gh#746): the SAME account carries trades taken under different modes — it was Practice, it is now Live.
        // A live-limit read must count ONLY the live trades: practice money was never at risk against the live limit,
        // and folding it in is the exact blending R-14 exists to prevent (a practice loss must not eat live headroom,
        // nor a practice win inflate it). Both trades are the same account, same Central day — only Mode separates them.
        await SeedTradeAsync(realizedPnL: -500m, closedAt: _now.AddHours(-2), mode: TradingMode.Practice);
        await SeedTradeAsync(realizedPnL: 100m, closedAt: _now.AddHours(-1), mode: TradingMode.Live);

        (await ReadAsync(mode: TradingMode.Live)).Should().Be(100m, "only the live trade counts toward a live read");
        (await ReadAsync(mode: TradingMode.Practice)).Should().Be(-500m, "only the practice trade counts toward a practice read");
    }
}

/// <summary>
/// The P&amp;L-by-day and day-detail read (gh#1062, R-8/R-9) — the same reader family as
/// <see cref="DailyRealizedReaderTests"/>, generalized from "today" to an arbitrary Central-day range / single day.
/// The behaviours that matter are the same ones the day-scoped reader already proved: Central-day grouping (not
/// UTC), realized-and-closed only, zero/empty (not absence) on a quiet window, R-20 owner scoping, and R-14
/// mode-scoping (gh#746) including the loud refusal of <see cref="TradingMode.Undeclared"/>.
/// </summary>
public class DailyRealizedByDayReaderTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private async Task SeedTradeAsync(
        decimal? realizedPnL, DateTimeOffset? closedAt, Guid? account = null, Guid? owner = null,
        TradingMode mode = TradingMode.Practice)
    {
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            AccountId = account ?? _account,
            Instrument = "CON.F.US.ES.U26",
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 5_300m,
            ExitPrice = closedAt is null ? null : 5_305m,
            RealizedPnL = realizedPnL,
            Mode = mode,
            ClosedAt = closedAt,
        });
        await context.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<DailyRealized>> ReadDaysAsync(
        DateOnly from, DateOnly to, Guid? asUser = null, TradingMode mode = TradingMode.Practice)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await context.RealizedPnLByDayForAccountAsync(_account, mode, from, to, CancellationToken.None);
    }

    private async Task<IReadOnlyList<Trade>> ReadDayAsync(
        DateOnly day, Guid? asUser = null, TradingMode mode = TradingMode.Practice)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await context.TradesForDayForAccountAsync(_account, mode, day, CancellationToken.None);
    }

    // -----------------------------------------------------------------------------------------------------------
    // RealizedPnLByDayForAccountAsync
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RealizedPnLByDay_ShouldGroupByCentralTradingDay_WithinTheRange()
    {
        // 06:00Z 08-03 = 01:00 CDT 08-03 (day 1); 18:00Z 08-04 = 13:00 CDT 08-04 (day 2).
        await SeedTradeAsync(realizedPnL: 100m, closedAt: new DateTimeOffset(2026, 8, 3, 6, 0, 0, TimeSpan.Zero));
        await SeedTradeAsync(realizedPnL: -30m, closedAt: new DateTimeOffset(2026, 8, 3, 20, 0, 0, TimeSpan.Zero));
        await SeedTradeAsync(realizedPnL: 50m, closedAt: new DateTimeOffset(2026, 8, 4, 18, 0, 0, TimeSpan.Zero));

        IReadOnlyList<DailyRealized> days = await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 4));

        days.Should().BeEquivalentTo(
            [
                new DailyRealized(new DateOnly(2026, 8, 3), 70m, 2),
                new DailyRealized(new DateOnly(2026, 8, 4), 50m, 1),
            ],
            options => options.WithStrictOrdering(), "grouped by Central trading day, earliest first");
    }

    [Fact]
    public async Task RealizedPnLByDay_ShouldExcludeATradeOutsideTheRange()
    {
        await SeedTradeAsync(realizedPnL: -999m, closedAt: new DateTimeOffset(2026, 8, 2, 18, 0, 0, TimeSpan.Zero)); // before `from`
        await SeedTradeAsync(realizedPnL: 40m, closedAt: new DateTimeOffset(2026, 8, 3, 18, 0, 0, TimeSpan.Zero));   // in range
        await SeedTradeAsync(realizedPnL: -999m, closedAt: new DateTimeOffset(2026, 8, 5, 18, 0, 0, TimeSpan.Zero)); // after `to`

        IReadOnlyList<DailyRealized> days = await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 3));

        days.Should().ContainSingle().Which.RealizedPnL.Should().Be(40m, "trades outside the range must not be summed in");
    }

    [Fact]
    public async Task RealizedPnLByDay_ShouldRespectTheCentralMidnightBoundary_AtRangeEdges()
    {
        // Central midnight for 08-03 is 05:00Z, so the window is [05:00Z 08-03, 05:00Z 08-05) (half-open, `to` is
        // 08-04). ±1 minute either side of an edge proves "near" the boundary but skips the literal edge value --
        // a >/>= or </<= off-by-one there would still pass unnoticed. So each edge gets three points: just
        // outside, AT the instant itself, and just inside (gh#1082).
        await SeedTradeAsync(realizedPnL: -1m, closedAt: new DateTimeOffset(2026, 8, 3, 4, 59, 0, TimeSpan.Zero)); // before windowStart, excluded
        await SeedTradeAsync(realizedPnL: 15m, closedAt: new DateTimeOffset(2026, 8, 3, 5, 0, 0, TimeSpan.Zero));  // AT windowStart, included
        await SeedTradeAsync(realizedPnL: 10m, closedAt: new DateTimeOffset(2026, 8, 3, 5, 1, 0, TimeSpan.Zero));  // after windowStart, included
        await SeedTradeAsync(realizedPnL: 20m, closedAt: new DateTimeOffset(2026, 8, 5, 4, 59, 0, TimeSpan.Zero)); // before windowEndExclusive, included
        await SeedTradeAsync(realizedPnL: -7m, closedAt: new DateTimeOffset(2026, 8, 5, 5, 0, 0, TimeSpan.Zero));  // AT windowEndExclusive, excluded
        await SeedTradeAsync(realizedPnL: -1m, closedAt: new DateTimeOffset(2026, 8, 5, 5, 1, 0, TimeSpan.Zero));  // after windowEndExclusive, excluded

        IReadOnlyList<DailyRealized> days = await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 4));

        days.Sum(day => day.RealizedPnL).Should().Be(45m, "only the trades landing inside the half-open Central-day range count");
        days.Sum(day => day.TradeCount).Should().Be(3);
    }

    [Fact]
    public async Task RealizedPnLByDay_ShouldRespectTheCentralMidnightBoundary_InWinter()
    {
        // Same exact-instant boundary proof as the summer case above, but Central midnight in CST (winter, UTC-6)
        // is 06:00Z, not 05:00Z -- a reader that hardcoded the summer offset instead of going through MarketClock
        // would pass that test and fail this one (gh#1082).
        await SeedTradeAsync(realizedPnL: 15m, closedAt: new DateTimeOffset(2026, 1, 15, 6, 0, 0, TimeSpan.Zero)); // AT windowStart, included
        await SeedTradeAsync(realizedPnL: -7m, closedAt: new DateTimeOffset(2026, 1, 16, 6, 0, 0, TimeSpan.Zero)); // AT windowEndExclusive, excluded

        IReadOnlyList<DailyRealized> days = await ReadDaysAsync(new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 15));

        days.Should().ContainSingle().Which.RealizedPnL.Should().Be(15m, "only the trade AT windowStart belongs to the requested day");
    }

    [Fact]
    public async Task RealizedPnLByDay_ShouldReturnEmpty_WhenNothingClosedInRange()
    {
        // A quiet window is an empty list, not an error or a fabricated zero-day -- there is no day to report.
        (await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 9))).Should().BeEmpty();
    }

    [Fact]
    public async Task RealizedPnLByDay_ShouldExcludeOpenAndUnrealizedTrades()
    {
        DateTimeOffset closedAt = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);
        await SeedTradeAsync(realizedPnL: null, closedAt: null);       // still open
        await SeedTradeAsync(realizedPnL: null, closedAt: closedAt);   // closed but no realized figure yet
        await SeedTradeAsync(realizedPnL: 25m, closedAt: closedAt);    // the only countable one

        IReadOnlyList<DailyRealized> days = await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 3));

        days.Should().ContainSingle().Which.Should().BeEquivalentTo(new DailyRealized(new DateOnly(2026, 8, 3), 25m, 1));
    }

    [Fact]
    public async Task RealizedPnLByDay_ShouldOnlyCountTheGivenAccount()
    {
        DateTimeOffset closedAt = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);
        await SeedTradeAsync(realizedPnL: 100m, closedAt: closedAt, account: _account);
        await SeedTradeAsync(realizedPnL: -999m, closedAt: closedAt, account: Guid.NewGuid());

        IReadOnlyList<DailyRealized> days = await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 3));

        days.Should().ContainSingle().Which.RealizedPnL.Should().Be(100m, "another account's trade is not this account's day");
    }

    [Fact]
    public async Task RealizedPnLByDay_ShouldOnlyCountTheCallersTrades()
    {
        // R-20: another operator's trade on the same account id is invisible.
        await SeedTradeAsync(realizedPnL: -777m, closedAt: new DateTimeOffset(2026, 8, 3, 18, 0, 0, TimeSpan.Zero), owner: _other);

        (await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 3), asUser: _operator)).Should().BeEmpty();
    }

    [Fact]
    public async Task RealizedPnLByDay_ShouldCountOnlyTheRequestedMode_WhenAnAccountChangedModes()
    {
        // R-14 (gh#746): a practice loss must never blend into a live read, or vice versa.
        DateTimeOffset closedAt = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);
        await SeedTradeAsync(realizedPnL: -500m, closedAt: closedAt, mode: TradingMode.Practice);
        await SeedTradeAsync(realizedPnL: 100m, closedAt: closedAt, mode: TradingMode.Live);

        IReadOnlyList<DailyRealized> live = await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 3), mode: TradingMode.Live);
        IReadOnlyList<DailyRealized> practice = await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 3), mode: TradingMode.Practice);

        live.Should().ContainSingle().Which.RealizedPnL.Should().Be(100m, "only the live trade counts toward a live read");
        practice.Should().ContainSingle().Which.RealizedPnL.Should().Be(-500m, "only the practice trade counts toward a practice read");
    }

    [Fact]
    public async Task RealizedPnLByDay_ShouldThrow_WhenTheModeIsUndeclared()
    {
        await SeedTradeAsync(realizedPnL: -400m, closedAt: new DateTimeOffset(2026, 8, 3, 18, 0, 0, TimeSpan.Zero), mode: TradingMode.Live);

        Func<Task> act = () => ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 3), mode: TradingMode.Undeclared);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task RealizedPnLByDay_ShouldThrow_WhenToIsDateOnlyMaxValue()
    {
        // gh#1087: `to.AddDays(1)` has no representable day after 9999-12-31. Guarded here -- alongside GuardMode --
        // so every caller of this reader inherits the refusal rather than hitting the AddDays throw bare.
        Func<Task> act = () => ReadDaysAsync(new DateOnly(2026, 8, 3), DateOnly.MaxValue);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // -----------------------------------------------------------------------------------------------------------
    // TradesForDayForAccountAsync
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TradesForDay_ShouldReturnTheDaysClosedRealizedTrades_OrderedByClosedAt()
    {
        DateTimeOffset later = new(2026, 8, 3, 20, 0, 0, TimeSpan.Zero);
        DateTimeOffset earlier = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);
        await SeedTradeAsync(realizedPnL: -30m, closedAt: later);
        await SeedTradeAsync(realizedPnL: 100m, closedAt: earlier);

        IReadOnlyList<Trade> trades = await ReadDayAsync(new DateOnly(2026, 8, 3));

        trades.Select(trade => trade.ClosedAt).Should().Equal([earlier, later], "the day's trades are returned in execution order");
    }

    [Fact]
    public async Task TradesForDay_ShouldExcludeAnAdjacentDaysTrades_AtTheCentralBoundary()
    {
        // Central midnight for 08-03 is 05:00Z, so the window is [05:00Z 08-03, 05:00Z 08-04) (half-open). ±1
        // minute either side of an edge proves "near" the boundary but skips the literal edge value -- a >/>= or
        // </<= off-by-one there would still pass unnoticed. So each edge gets three points: just outside, AT the
        // instant itself, and just inside (gh#1082).
        await SeedTradeAsync(realizedPnL: -500m, closedAt: new DateTimeOffset(2026, 8, 3, 4, 59, 0, TimeSpan.Zero)); // before windowStart, excluded
        await SeedTradeAsync(realizedPnL: 15m, closedAt: new DateTimeOffset(2026, 8, 3, 5, 0, 0, TimeSpan.Zero));    // AT windowStart, included
        await SeedTradeAsync(realizedPnL: -40m, closedAt: new DateTimeOffset(2026, 8, 3, 5, 1, 0, TimeSpan.Zero));   // after windowStart, included
        await SeedTradeAsync(realizedPnL: -7m, closedAt: new DateTimeOffset(2026, 8, 4, 5, 0, 0, TimeSpan.Zero));    // AT windowEndExclusive, excluded
        await SeedTradeAsync(realizedPnL: -600m, closedAt: new DateTimeOffset(2026, 8, 4, 5, 1, 0, TimeSpan.Zero)); // after windowEndExclusive, excluded

        IReadOnlyList<Trade> trades = await ReadDayAsync(new DateOnly(2026, 8, 3));

        trades.Select(trade => trade.RealizedPnL).Should().Equal([15m, -40m], "only the two trades inside the half-open Central day belong to it");
    }

    [Fact]
    public async Task TradesForDay_ShouldExcludeAnAdjacentDaysTrades_InWinter()
    {
        // Same exact-instant boundary proof as the summer case above, but Central midnight in CST (winter, UTC-6)
        // is 06:00Z, not 05:00Z -- a reader that hardcoded the summer offset instead of going through MarketClock
        // would pass that test and fail this one (gh#1082).
        await SeedTradeAsync(realizedPnL: 15m, closedAt: new DateTimeOffset(2026, 1, 15, 6, 0, 0, TimeSpan.Zero)); // AT windowStart, included
        await SeedTradeAsync(realizedPnL: -7m, closedAt: new DateTimeOffset(2026, 1, 16, 6, 0, 0, TimeSpan.Zero)); // AT windowEndExclusive, excluded

        IReadOnlyList<Trade> trades = await ReadDayAsync(new DateOnly(2026, 1, 15));

        trades.Should().ContainSingle().Which.RealizedPnL.Should().Be(15m, "only the trade AT windowStart belongs to the requested day");
    }

    [Fact]
    public async Task TradesForDay_ShouldExcludeOpenAndUnrealizedTrades()
    {
        DateTimeOffset closedAt = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);
        await SeedTradeAsync(realizedPnL: null, closedAt: null);     // still open
        await SeedTradeAsync(realizedPnL: null, closedAt: closedAt); // closed but no realized figure yet
        await SeedTradeAsync(realizedPnL: 25m, closedAt: closedAt);  // the only countable one

        IReadOnlyList<Trade> trades = await ReadDayAsync(new DateOnly(2026, 8, 3));

        trades.Should().ContainSingle().Which.RealizedPnL.Should().Be(25m);
    }

    [Fact]
    public async Task TradesForDay_ShouldReturnEmpty_WhenNothingClosedThatDay()
    {
        (await ReadDayAsync(new DateOnly(2026, 8, 3))).Should().BeEmpty("a quiet day is zero trades, not an error");
    }

    [Fact]
    public async Task TradesForDay_ShouldOnlyCountTheGivenAccount()
    {
        DateTimeOffset closedAt = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);
        await SeedTradeAsync(realizedPnL: 100m, closedAt: closedAt, account: _account);
        await SeedTradeAsync(realizedPnL: -999m, closedAt: closedAt, account: Guid.NewGuid());

        IReadOnlyList<Trade> trades = await ReadDayAsync(new DateOnly(2026, 8, 3));

        trades.Should().ContainSingle().Which.RealizedPnL.Should().Be(100m, "another account's trade is not this account's day");
    }

    [Fact]
    public async Task TradesForDay_ShouldOnlyCountTheCallersTrades()
    {
        // R-20: another operator's trade on the same account id is invisible.
        await SeedTradeAsync(realizedPnL: -777m, closedAt: new DateTimeOffset(2026, 8, 3, 18, 0, 0, TimeSpan.Zero), owner: _other);

        (await ReadDayAsync(new DateOnly(2026, 8, 3), asUser: _operator)).Should().BeEmpty();
    }

    [Fact]
    public async Task TradesForDay_ShouldCountOnlyTheRequestedMode_WhenAnAccountChangedModes()
    {
        // R-14 (gh#746): a practice loss must never blend into a live day-detail read, or vice versa.
        DateTimeOffset closedAt = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);
        await SeedTradeAsync(realizedPnL: -500m, closedAt: closedAt, mode: TradingMode.Practice);
        await SeedTradeAsync(realizedPnL: 100m, closedAt: closedAt, mode: TradingMode.Live);

        IReadOnlyList<Trade> live = await ReadDayAsync(new DateOnly(2026, 8, 3), mode: TradingMode.Live);
        IReadOnlyList<Trade> practice = await ReadDayAsync(new DateOnly(2026, 8, 3), mode: TradingMode.Practice);

        live.Should().ContainSingle().Which.RealizedPnL.Should().Be(100m, "only the live trade counts toward a live read");
        practice.Should().ContainSingle().Which.RealizedPnL.Should().Be(-500m, "only the practice trade counts toward a practice read");
    }

    [Fact]
    public async Task TradesForDay_ShouldThrow_WhenTheModeIsUndeclared()
    {
        await SeedTradeAsync(realizedPnL: -400m, closedAt: new DateTimeOffset(2026, 8, 3, 18, 0, 0, TimeSpan.Zero), mode: TradingMode.Live);

        Func<Task> act = () => ReadDayAsync(new DateOnly(2026, 8, 3), mode: TradingMode.Undeclared);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task TradesForDay_ShouldThrow_WhenDayIsDateOnlyMaxValue()
    {
        // gh#1087: `day.AddDays(1)` has no representable day after 9999-12-31. Guarded here -- alongside GuardMode --
        // so every caller of this reader inherits the refusal rather than hitting the AddDays throw bare.
        Func<Task> act = () => ReadDayAsync(DateOnly.MaxValue);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
