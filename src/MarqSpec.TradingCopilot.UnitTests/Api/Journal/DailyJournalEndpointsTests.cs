using MarqSpec.TradingCopilot.Api.Journal;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Journal;

/// <summary>
/// The day-realized / day-detail read surface (gh#1062, R-8/R-9) — <c>GET /accounts/{id}/journal/daily</c> (the
/// P&amp;L-by-day calendar) and <c>GET /accounts/{id}/journal/daily/{date}</c> (drilling into one day's trades). The
/// sole blocker on gh#659 (the journal read surface). Reuses <c>DailyRealizedReader</c> rather than a second
/// definition of realized P&amp;L; the behaviours that matter mirror <c>RiskHeadroomTests</c>: the account's CURRENT
/// mode scopes the read (R-14, gh#746 class of defect), an undeclared / missing account is a 404 (never a fabricated
/// figure), and a quiet window reads as zero / empty, never absence.
/// </summary>
public class DailyJournalEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();

    // 2026-08-03 18:00 UTC = 13:00 CDT. The Central day is 2026-08-03; its midnight is 05:00 UTC.
    private static readonly DateTimeOffset _now = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static DailyRealizedPnLListResponse DaysOf(IResult result) =>
        (DailyRealizedPnLListResponse)((IValueHttpResult)result).Value!;

    private static DayDetailResponse DetailOf(IResult result) =>
        (DayDetailResponse)((IValueHttpResult)result).Value!;

    private async Task SeedAccountAsync(TradingMode mode = TradingMode.Practice, Guid? owner = null, Guid? account = null)
    {
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Accounts.Add(new Account
        {
            Id = account ?? _account,
            UserId = ownerId,
            ConnectionId = Guid.NewGuid(),
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Mode = mode,
        });
        await context.SaveChangesAsync();
    }

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

    private async Task<IResult> ReadDaysAsync(DateOnly? from = null, DateOnly? to = null, Guid? asUser = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await DailyJournalEndpoints.GetDailyRealizedAsync(_account, from, to, _now, context, CancellationToken.None);
    }

    private async Task<IResult> ReadDayAsync(DateOnly date, Guid? asUser = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await DailyJournalEndpoints.GetDayDetailAsync(_account, date, context, CancellationToken.None);
    }

    // -----------------------------------------------------------------------------------------------------------
    // GET /accounts/{id}/journal/daily -- P&L by day
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetDailyRealized_ShouldReturnNotFound_WhenTheAccountDoesNotExist()
    {
        StatusOf(await ReadDaysAsync()).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetDailyRealized_ShouldReturnNotFound_WhenTheAccountIsUndeclared()
    {
        // gh#746 class: an Undeclared account trades nowhere and has no journal to scope a mode-filtered read by.
        await SeedAccountAsync(mode: TradingMode.Undeclared);
        await SeedTradeAsync(realizedPnL: -400m, closedAt: _now.AddHours(-1), mode: TradingMode.Live);

        StatusOf(await ReadDaysAsync()).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetDailyRealized_ShouldReturnDaysWithinTheGivenRange()
    {
        await SeedAccountAsync();
        await SeedTradeAsync(realizedPnL: 100m, closedAt: new DateTimeOffset(2026, 8, 3, 18, 0, 0, TimeSpan.Zero));
        await SeedTradeAsync(realizedPnL: -30m, closedAt: new DateTimeOffset(2026, 8, 4, 18, 0, 0, TimeSpan.Zero));

        IResult result = await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 4));

        DailyRealizedPnLListResponse body = DaysOf(result);
        body.Days.Should().BeEquivalentTo(
            [
                new DailyRealizedPnLResponse(new DateOnly(2026, 8, 3), 100m, 1),
                new DailyRealizedPnLResponse(new DateOnly(2026, 8, 4), -30m, 1),
            ],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task GetDailyRealized_ShouldDefaultToTheCurrentCentralMonth_WhenNoRangeIsGiven()
    {
        await SeedAccountAsync();
        await SeedTradeAsync(realizedPnL: 100m, closedAt: _now); // 2026-08-03, inside the current Central month
        await SeedTradeAsync(realizedPnL: -999m, closedAt: new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero)); // prior month

        IResult result = await ReadDaysAsync();

        DaysOf(result).Days.Should().ContainSingle().Which.RealizedPnL.Should().Be(
            100m, "no range given defaults to the current Central month, excluding the prior month's trade");
    }

    [Fact]
    public async Task GetDailyRealized_ShouldReturnEmptyDays_OnAQuietWindow()
    {
        await SeedAccountAsync();

        DaysOf(await ReadDaysAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31))).Days.Should().BeEmpty(
            "a quiet window is an empty list, not an error");
    }

    [Fact]
    public async Task GetDailyRealized_ShouldRejectAnInvertedRange()
    {
        await SeedAccountAsync();

        IResult result = await ReadDaysAsync(new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 1));

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetDailyRealized_ShouldReturnBadRequest_WhenToIsAtDateOnlyMaxValue()
    {
        // gh#1087: `to.AddDays(1)` inside RealizedPnLByDayForAccountAsync throws for DateOnly.MaxValue (there is no
        // day after 9999-12-31), and the API registers no exception middleware -- an unguarded caller-supplied
        // MaxValue must 400 here rather than let that throw reach the caller as an unhandled 500. `from` is left
        // unset, exactly gh#1087's repro: the default `from` (the 1st of that month) keeps windowFrom <= windowTo,
        // so the existing inverted-range 400 above does NOT fire and this guard is the only thing standing between
        // the caller and the throw.
        await SeedAccountAsync();

        IResult result = await ReadDaysAsync(to: DateOnly.MaxValue);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetDailyRealized_ShouldReturnOk_WhenFromIsDateOnlyMinValue()
    {
        // DateOnly.MinValue is already safe at the `from` end (no AddDays applied to it) -- pinned so a later
        // upper-bound clamp does not get generalized into over-rejecting the lower bound too (gh#1087).
        await SeedAccountAsync();

        IResult result = await ReadDaysAsync(from: DateOnly.MinValue);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task GetDailyRealized_ShouldCountOnlyTheAccountsCurrentMode_WhenItChangedModes()
    {
        // R-14 (gh#746): a leftover practice loss must never blend into a now-live account's P&L-by-day read.
        await SeedAccountAsync(mode: TradingMode.Live);
        await SeedTradeAsync(realizedPnL: -500m, closedAt: _now, mode: TradingMode.Practice);
        await SeedTradeAsync(realizedPnL: -200m, closedAt: _now, mode: TradingMode.Live);

        DailyRealizedPnLListResponse body = DaysOf(await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 3)));

        body.Days.Should().ContainSingle().Which.RealizedPnL.Should().Be(-200m, "only the live trade counts toward a live account's read");
    }

    [Fact]
    public async Task GetDailyRealized_ShouldOnlyCountTheCallersTrades()
    {
        // R-20: another operator's trade on the same account id is invisible.
        await SeedAccountAsync();
        await SeedTradeAsync(realizedPnL: -777m, closedAt: _now, owner: _other);

        DaysOf(await ReadDaysAsync(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 3), asUser: _operator)).Days.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDailyRealized_ShouldReturn404_WhenAStrangerReadsAnotherOperatorsAccount()
    {
        await SeedAccountAsync(owner: _operator);

        StatusOf(await ReadDaysAsync(asUser: _other)).Should().Be(
            StatusCodes.Status404NotFound, "a stranger reading another operator's account must get nothing that isn't theirs");
    }

    // -----------------------------------------------------------------------------------------------------------
    // GET /accounts/{id}/journal/daily/{date} -- day detail
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetDayDetail_ShouldReturnNotFound_WhenTheAccountDoesNotExist()
    {
        StatusOf(await ReadDayAsync(new DateOnly(2026, 8, 3))).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetDayDetail_ShouldReturnNotFound_WhenTheAccountIsUndeclared()
    {
        await SeedAccountAsync(mode: TradingMode.Undeclared);
        await SeedTradeAsync(realizedPnL: -400m, closedAt: _now, mode: TradingMode.Live);

        StatusOf(await ReadDayAsync(new DateOnly(2026, 8, 3))).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetDayDetail_ShouldReturnBadRequest_WhenDateIsAtDateOnlyMaxValue()
    {
        // gh#1087: `day.AddDays(1)` inside TradesForDayForAccountAsync throws for DateOnly.MaxValue (there is no day
        // after 9999-12-31), and the API registers no exception middleware -- GET .../journal/daily/9999-12-31 must
        // 400 here rather than let that throw reach the caller as an unhandled 500.
        await SeedAccountAsync();

        StatusOf(await ReadDayAsync(DateOnly.MaxValue)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetDayDetail_ShouldReturnOk_WhenDateIsDateOnlyMinValue()
    {
        // DateOnly.MinValue is already safe here (no AddDays applied to it directly) -- pinned so a later
        // upper-bound clamp does not get generalized into over-rejecting the lower bound too (gh#1087).
        await SeedAccountAsync();

        StatusOf(await ReadDayAsync(DateOnly.MinValue)).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task GetDayDetail_ShouldReturnTheDaysTradesAndTheirRealizedSum()
    {
        await SeedAccountAsync();
        await SeedTradeAsync(realizedPnL: 100m, closedAt: _now.AddHours(-2));
        await SeedTradeAsync(realizedPnL: -30m, closedAt: _now.AddHours(-1));

        DayDetailResponse detail = DetailOf(await ReadDayAsync(new DateOnly(2026, 8, 3)));

        detail.Date.Should().Be(new DateOnly(2026, 8, 3));
        detail.RealizedPnL.Should().Be(70m);
        detail.Trades.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetDayDetail_ShouldReturnEmpty_WhenNothingClosedThatDay()
    {
        await SeedAccountAsync();

        DayDetailResponse detail = DetailOf(await ReadDayAsync(new DateOnly(2026, 8, 3)));

        detail.RealizedPnL.Should().Be(0m, "a quiet day is zero, never absence");
        detail.Trades.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDayDetail_ShouldExcludeAnAdjacentDaysTrade_AtTheCentralBoundary()
    {
        await SeedAccountAsync();
        // Central midnight for 08-03 is 05:00Z: one minute before is 08-02 (excluded), one minute after is 08-03.
        await SeedTradeAsync(realizedPnL: -500m, closedAt: new DateTimeOffset(2026, 8, 3, 4, 59, 0, TimeSpan.Zero));
        await SeedTradeAsync(realizedPnL: -40m, closedAt: new DateTimeOffset(2026, 8, 3, 5, 1, 0, TimeSpan.Zero));

        DayDetailResponse detail = DetailOf(await ReadDayAsync(new DateOnly(2026, 8, 3)));

        detail.RealizedPnL.Should().Be(-40m, "only the trade after Central midnight belongs to this day");
    }

    [Fact]
    public async Task GetDayDetail_ShouldCountOnlyTheAccountsCurrentMode_WhenItChangedModes()
    {
        // R-14 (gh#746): a leftover practice loss must never blend into a now-live account's day detail.
        await SeedAccountAsync(mode: TradingMode.Live);
        await SeedTradeAsync(realizedPnL: -500m, closedAt: _now, mode: TradingMode.Practice);
        await SeedTradeAsync(realizedPnL: -200m, closedAt: _now, mode: TradingMode.Live);

        DayDetailResponse detail = DetailOf(await ReadDayAsync(new DateOnly(2026, 8, 3)));

        detail.RealizedPnL.Should().Be(-200m, "only the live trade counts toward a live account's day detail");
        detail.Trades.Should().ContainSingle();
    }

    [Fact]
    public async Task GetDayDetail_ShouldOnlyCountTheCallersTrades()
    {
        // R-20: another operator's trade on the same account id is invisible.
        await SeedAccountAsync();
        await SeedTradeAsync(realizedPnL: -777m, closedAt: _now, owner: _other);

        DayDetailResponse detail = DetailOf(await ReadDayAsync(new DateOnly(2026, 8, 3), asUser: _operator));

        detail.Trades.Should().BeEmpty();
        detail.RealizedPnL.Should().Be(0m);
    }

    [Fact]
    public async Task GetDayDetail_ShouldReturn404_WhenAStrangerReadsAnotherOperatorsAccount()
    {
        await SeedAccountAsync(owner: _operator);

        StatusOf(await ReadDayAsync(new DateOnly(2026, 8, 3), asUser: _other)).Should().Be(StatusCodes.Status404NotFound);
    }
}
