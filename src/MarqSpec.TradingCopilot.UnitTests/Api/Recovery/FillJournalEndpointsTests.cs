using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The <c>GET /accounts/{id}/fills</c> shape (gh#792): the four client-mistake / degraded outcomes the journaled-
/// fills read keeps distinct — a required, valid instrument; a non-empty window; a too-wide window refused not
/// truncated; a stranger's account 404'd — and the 200 body the chart's fill-marker overlay consumes.
/// </summary>
public class FillJournalEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _accountId = Guid.NewGuid();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(_operator));

    private FillJournalReadService Reader() => new(Context());

    private static DateTimeOffset At(int hour) => new(2026, 7, 28, hour, 0, 0, TimeSpan.Zero);

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private async Task SeedAccountAsync()
    {
        await using TradingCopilotDbContext seed = Context();
        seed.Accounts.Add(new Account
        {
            Id = _accountId,
            UserId = _operator,
            ConnectionId = Guid.NewGuid(),
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
        });
        await seed.SaveChangesAsync();
    }

    private async Task SeedFillsAsync(int count)
    {
        await using TradingCopilotDbContext seed = Context();
        for (int i = 0; i < count; i++)
        {
            Guid orderId = Guid.NewGuid();
            seed.Orders.Add(new Order
            {
                Id = orderId,
                UserId = _operator,
                AccountId = _accountId,
                Instrument = "CON.F.US.MES.U26",
                Symbol = "ES",
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Market,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                PlacedAt = At(10),
            });
            seed.Fills.Add(new Fill
            {
                Id = Guid.NewGuid(),
                UserId = _operator,
                OrderId = orderId,
                Price = 5300m,
                Size = 1,
                ExecutedAt = At(10).AddSeconds(i),
            });
        }
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task ReadAsync_ShouldBe400_WhenTheInstrumentIsMissing()
    {
        // The overlay always charts one instrument, so the scope is required — a missing one is a client mistake.
        IResult result = await FillJournalEndpoints.ReadAsync(
            Guid.NewGuid(), instrument: null, At(9), At(11), Reader(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ReadAsync_ShouldBe400_WhenTheInstrumentIsBlank()
    {
        IResult result = await FillJournalEndpoints.ReadAsync(
            Guid.NewGuid(), "   ", At(9), At(11), Reader(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ReadAsync_ShouldBe400_WhenTheWindowIsEmpty()
    {
        // from >= to is an empty half-open window — a malformed request, not "no fills".
        IResult result = await FillJournalEndpoints.ReadAsync(
            Guid.NewGuid(), "ES", At(11), At(9), Reader(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ReadAsync_ShouldBe404_WhenTheAccountIsNotFoundOrNotOwned()
    {
        // R-20 default-deny: an unseeded / unowned account is indistinguishable from one that does not exist.
        IResult result = await FillJournalEndpoints.ReadAsync(
            Guid.NewGuid(), "ES", At(9), At(11), Reader(), CancellationToken.None);

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task ReadAsync_ShouldBe400_WhenTheWindowIsTooWide()
    {
        await SeedAccountAsync();
        await SeedFillsAsync(FillJournalReadService.MaxFills + 1);

        IResult result = await FillJournalEndpoints.ReadAsync(
            _accountId, "ES", At(9), At(11), Reader(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ReadAsync_ShouldBe200_WithTheFills_OnAnOwnedInstrumentWindow()
    {
        await SeedAccountAsync();
        await SeedFillsAsync(2);

        IResult result = await FillJournalEndpoints.ReadAsync(
            _accountId, "ES", At(9), At(11), Reader(), CancellationToken.None);

        result.Should().BeOfType<Ok<FillsResponse>>()
            .Which.Value!.Fills.Should().HaveCount(2);
    }

    [Fact]
    public void FillsResponse_ShouldCarryTheFills()
    {
        FillsResponse response = new([new ExecutedFill("F-1", "Buy", 5300m, 2, 1.25m, At(10))]);

        response.Fills.Should().ContainSingle();
        response.Fills[0].Side.Should().Be("Buy");
        response.Fills[0].Price.Should().Be(5300m);
    }
}
