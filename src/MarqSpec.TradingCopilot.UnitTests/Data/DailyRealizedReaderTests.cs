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
