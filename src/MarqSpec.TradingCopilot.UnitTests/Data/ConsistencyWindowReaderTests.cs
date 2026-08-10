using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Data;

/// <summary>
/// The consistency-window reader (gh#380) — the profit picture the send-path consistency gate is measured against.
/// The behaviours that matter: it sums realized profit across the account's closed trades and takes the single best
/// <b>Central</b> day (not UTC), excludes open / unrealized rows, is <b>Empty</b> when nothing closed, is scoped to
/// the caller (R-20) and the account, and — the reason this suite exists (gh#746) — counts only trades taken under
/// the requested <see cref="TradingMode"/>, so a practice result on a now-live account never feeds a live limit (R-14).
/// </summary>
public class ConsistencyWindowReaderTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    // Two distinct Central trading days. 16:00 UTC = 11:00 CDT, so both fall on their UTC date here (well clear of the
    // 05:00-UTC Central midnight), letting the day grouping be read off the calendar date without boundary subtlety.
    private static readonly DateTimeOffset _dayA = new(2026, 8, 3, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _dayB = new(2026, 8, 4, 16, 0, 0, TimeSpan.Zero);

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

    private async Task<ConsistencyWindow> ReadAsync(Guid? asUser = null, TradingMode mode = TradingMode.Practice)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await context.ConsistencyWindowForAccountAsync(_account, mode, CancellationToken.None);
    }

    [Fact]
    public async Task ConsistencyWindow_ShouldSumAcrossDaysAndTakeTheBestDay()
    {
        await SeedTradeAsync(realizedPnL: 100m, closedAt: _dayA);
        await SeedTradeAsync(realizedPnL: 200m, closedAt: _dayA.AddHours(1)); // still day A -> day A total 300
        await SeedTradeAsync(realizedPnL: 50m, closedAt: _dayB);             // day B total 50

        ConsistencyWindow window = await ReadAsync();

        window.CumulativeProfit.Should().Be(350m);
        window.BestDayProfit.Should().Be(300m, "day A's 300 is the largest single Central day");
    }

    [Fact]
    public async Task ConsistencyWindow_ShouldCountOnlyTheRequestedMode_WhenAnAccountChangedModes()
    {
        // R-14 (gh#746): the same account holds a practice day and a live day. A live read must see only the live
        // day, and a practice read only the practice day — the consistency rule is a per-mode measure, so blending
        // them would let a practice day disqualify (or rescue) a live payout it was never part of.
        await SeedTradeAsync(realizedPnL: 500m, closedAt: _dayA, mode: TradingMode.Practice);
        await SeedTradeAsync(realizedPnL: 100m, closedAt: _dayB, mode: TradingMode.Live);

        ConsistencyWindow live = await ReadAsync(mode: TradingMode.Live);
        live.CumulativeProfit.Should().Be(100m);
        live.BestDayProfit.Should().Be(100m);

        ConsistencyWindow practice = await ReadAsync(mode: TradingMode.Practice);
        practice.CumulativeProfit.Should().Be(500m);
        practice.BestDayProfit.Should().Be(500m);
    }

    [Fact]
    public async Task ConsistencyWindow_ShouldBeEmpty_WhenNothingClosedInThatMode()
    {
        // Trades exist, but none in the requested mode -> the rule cannot bind, exactly as a fresh account.
        await SeedTradeAsync(realizedPnL: 900m, closedAt: _dayA, mode: TradingMode.Practice);

        (await ReadAsync(mode: TradingMode.Live)).Should().Be(ConsistencyWindow.Empty);
    }

    [Fact]
    public async Task ConsistencyWindow_ShouldExcludeOpenAndUnrealizedTrades()
    {
        await SeedTradeAsync(realizedPnL: null, closedAt: null);            // still open
        await SeedTradeAsync(realizedPnL: null, closedAt: _dayA);           // closed but no realized figure yet
        await SeedTradeAsync(realizedPnL: 25m, closedAt: _dayA.AddHours(1)); // the only countable one

        ConsistencyWindow window = await ReadAsync();

        window.CumulativeProfit.Should().Be(25m);
        window.BestDayProfit.Should().Be(25m);
    }

    [Fact]
    public async Task ConsistencyWindow_ShouldOnlyCountTheGivenAccount()
    {
        await SeedTradeAsync(realizedPnL: 100m, closedAt: _dayA, account: _account);
        await SeedTradeAsync(realizedPnL: 999m, closedAt: _dayA, account: Guid.NewGuid());

        (await ReadAsync()).CumulativeProfit.Should().Be(100m, "another account's trade is not this account's window");
    }

    [Fact]
    public async Task ConsistencyWindow_ShouldOnlyCountTheCallersTrades()
    {
        // R-20: the reader is a request path (send-time gate), so the query filter scopes to the caller. Another
        // operator's trade on the same account id is invisible.
        await SeedTradeAsync(realizedPnL: 777m, closedAt: _dayA, owner: _other);

        (await ReadAsync(asUser: _operator)).Should().Be(ConsistencyWindow.Empty);
    }
}
