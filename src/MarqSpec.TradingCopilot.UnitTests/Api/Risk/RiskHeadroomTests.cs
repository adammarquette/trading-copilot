using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Risk;

/// <summary>
/// The daily-headroom read surface (gh#587, R-5): <c>GET /accounts/{id}/risk/headroom</c> — today's remaining daily
/// loss budget and target progress, readable WITHOUT a send. The behaviours that matter: it is the <b>same</b>
/// <see cref="DailyHeadroom"/> arithmetic and the <b>same persisted limits</b> the send-time gate uses (never a second
/// definition, R-5); a declared-but-quiet day reads FULL headroom (distinct from the 404 an undeclared account gets);
/// and today's realized loss reduces it, spending it when it reaches the governor.
/// </summary>
public class RiskHeadroomTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private static readonly DateTimeOffset _now = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static DailyHeadroomResponse HeadroomOf(IResult result) =>
        (DailyHeadroomResponse)((IValueHttpResult)result).Value!;

    private async Task SeedProfileAsync(
        decimal governor = 600m,
        decimal? dailyLossLimit = null,
        decimal? dailyProfitTarget = null,
        bool stopForDay = false,
        Guid? owner = null)
    {
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.RiskProfiles.Add(new RiskProfileRecord
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            AccountId = _account,
            StartingBalance = 50_000m,
            FloorSource = FloorSource.FirmImposed,
            TrailingMode = TrailingMode.EndOfDay,
            TrailingAmount = 2_000m,
            PerTradeRiskFraction = 0.10m,
            TargetRewardRatio = 1.5m,
            MaxDrawdownPerTrade = 300m,
            DailyDrawdownGovernor = governor,
            DailyLossLimit = dailyLossLimit,
            DailyProfitTarget = dailyProfitTarget,
            StopForDayAtProfitTarget = stopForDay,
            SizingBasis = SizingBasis.SafetyStop,
            MaxContractsPerOrder = 3,
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedTradeAsync(decimal realizedPnL, Guid? owner = null)
    {
        Guid ownerId = owner ?? _operator;
        await using TradingCopilotDbContext context = Context(ownerId);
        context.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            AccountId = _account,
            Instrument = "CON.F.US.ES.U26",
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 5_300m,
            ExitPrice = 5_305m,
            RealizedPnL = realizedPnL,
            Mode = TradingMode.Practice,
            ClosedAt = _now.AddHours(-1),
        });
        await context.SaveChangesAsync();
    }

    private async Task<IResult> ReadAsync(Guid? asUser = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await RiskEndpoints.GetHeadroomAsync(_account, _now, context, CancellationToken.None);
    }

    [Fact]
    public async Task GetHeadroom_ShouldReturnNotFound_WhenNoProfileDeclared()
    {
        // No governor to project. Absence IS the answer, exactly as GET /risk -- distinct from a quiet day (below).
        StatusOf(await ReadAsync()).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetHeadroom_ShouldReadFullHeadroom_OnAQuietDay()
    {
        await SeedProfileAsync(governor: 600m);

        DailyHeadroomResponse headroom = HeadroomOf(await ReadAsync());

        headroom.DayLoss.Should().Be(0m);
        headroom.UnderGovernor.Should().Be(600m, "a declared profile with no trades today reads full, not absent");
        headroom.GovernorSpent.Should().BeFalse();
    }

    [Fact]
    public async Task GetHeadroom_ShouldReduceHeadroom_ByTodaysRealizedLoss()
    {
        await SeedProfileAsync(governor: 600m);
        await SeedTradeAsync(realizedPnL: 100m);
        await SeedTradeAsync(realizedPnL: -300m); // net -200 today

        DailyHeadroomResponse headroom = HeadroomOf(await ReadAsync());

        headroom.DayLoss.Should().Be(200m);
        headroom.UnderGovernor.Should().Be(400m);
        headroom.GovernorSpent.Should().BeFalse();
    }

    [Fact]
    public async Task GetHeadroom_ShouldReportGovernorSpent_WhenTheDayLossReachesIt()
    {
        await SeedProfileAsync(governor: 600m);
        await SeedTradeAsync(realizedPnL: -650m);

        DailyHeadroomResponse headroom = HeadroomOf(await ReadAsync());

        headroom.DayLoss.Should().Be(650m);
        headroom.UnderGovernor.Should().Be(-50m);
        headroom.GovernorSpent.Should().BeTrue("the governor is fully spent -- the gate's DailyGovernor refusal");
    }

    [Fact]
    public async Task GetHeadroom_ShouldComputeTheHardLossLimit_WhenSet_AndNullOtherwise()
    {
        await SeedProfileAsync(governor: 600m, dailyLossLimit: 1_000m);
        await SeedTradeAsync(realizedPnL: -200m);

        DailyHeadroomResponse withLimit = HeadroomOf(await ReadAsync());
        withLimit.UnderDailyLossLimit.Should().Be(800m);
        withLimit.DailyLossLimitSpent.Should().BeFalse();
    }

    [Fact]
    public async Task GetHeadroom_ShouldReturnANullLossLimit_WhenNoneIsImposed()
    {
        // An Apex intraday-trail account imposes no hard daily loss limit; null is honest absence, never unlimited room.
        await SeedProfileAsync(governor: 600m, dailyLossLimit: null);
        await SeedTradeAsync(realizedPnL: -200m);

        HeadroomOf(await ReadAsync()).UnderDailyLossLimit.Should().BeNull();
    }

    [Fact]
    public async Task GetHeadroom_ShouldReportTargetProgress_AndReached_WhenStandDownEnabled()
    {
        await SeedProfileAsync(governor: 600m, dailyProfitTarget: 500m, stopForDay: true);
        await SeedTradeAsync(realizedPnL: 500m);

        DailyHeadroomResponse headroom = HeadroomOf(await ReadAsync());

        headroom.DayRealizedProfit.Should().Be(500m);
        headroom.DailyProfitTarget.Should().Be(500m);
        headroom.DailyTargetReached.Should().BeTrue("realized profit reached the target and stand-down is enabled");
        headroom.DayLoss.Should().Be(0m, "a profitable day consumes no loss headroom");
    }

    [Fact]
    public async Task GetHeadroom_ShouldNotReportTargetReached_BelowTheTarget()
    {
        await SeedProfileAsync(governor: 600m, dailyProfitTarget: 500m, stopForDay: true);
        await SeedTradeAsync(realizedPnL: 499m);

        HeadroomOf(await ReadAsync()).DailyTargetReached.Should().BeFalse();
    }

    [Fact]
    public async Task GetHeadroom_ShouldUseTheSameInputsAndArithmeticAsTheGate()
    {
        // The against-the-gate proof. The gate resolves its daily limits through the passthrough mappings (RiskGate
        // reads context.Rules.DailyLossLimit and profile.DailyDrawdownGovernor); the read surface reads the record
        // fields directly. Pin they are the SAME, so the read can never become a second definition of the limit --
        // and that the headroom equals DailyHeadroom.Remaining fed those same inputs (the shared arithmetic).
        await SeedProfileAsync(governor: 600m, dailyLossLimit: 1_000m);
        await SeedTradeAsync(realizedPnL: -200m);

        await using TradingCopilotDbContext context = Context();
        RiskProfileRecord record = await context.RiskProfiles.SingleAsync(profile => profile.AccountId == _account);

        record.ToRiskProfile().DailyDrawdownGovernor.Should().Be(record.DailyDrawdownGovernor);
        record.ToAccountRiskRules().DailyLossLimit.Should().Be(record.DailyLossLimit);

        DailyHeadroom gate = DailyHeadroom.Remaining(record.DailyLossLimit, record.DailyDrawdownGovernor, dayLoss: 200m);
        DailyHeadroomResponse read = HeadroomOf(await ReadAsync());
        read.UnderGovernor.Should().Be(gate.UnderGovernor);
        read.UnderDailyLossLimit.Should().Be(gate.UnderDailyLossLimit);
    }

    [Fact]
    public async Task GetHeadroom_ShouldOnlyCountTheCallersTrades()
    {
        // R-20: another operator's losing trade on this account is invisible to the caller's headroom read.
        await SeedProfileAsync(governor: 600m);
        await SeedTradeAsync(realizedPnL: -500m, owner: _other);

        HeadroomOf(await ReadAsync(asUser: _operator)).DayLoss.Should().Be(0m);
    }
}
