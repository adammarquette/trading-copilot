using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The journaled-fills read (gh#792): the operator's fills on one account + instrument over a time window, read
/// from the durable journal (<see cref="Fill"/> joined to its <see cref="Order"/>) for the gh#727 chart overlay.
/// </summary>
/// <remarks>
/// The behaviours guarded: instrument scoping matches the order's venue-neutral <b>symbol</b> (an order with no
/// symbol is excluded); the window is half-open <c>[from, to)</c>; a window past the cap is <b>refused, not
/// truncated</b>; and R-20 hides a stranger's account (the endpoint maps the null to 404).
/// </remarks>
public class FillJournalReadServiceTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Guid _accountId = Guid.NewGuid();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? user = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(user ?? _operator));

    private static InstrumentId Es => InstrumentId.Parse("ES");

    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 7, 28, hour, minute, 0, TimeSpan.Zero);

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

    private async Task SeedFillAsync(
        string? symbol,
        decimal price,
        int size,
        DateTimeOffset at,
        OrderSide side = OrderSide.Buy,
        Guid? owner = null,
        Guid? accountId = null)
    {
        Guid actor = owner ?? _operator;
        await using TradingCopilotDbContext seed = Context(actor);
        Guid orderId = Guid.NewGuid();
        seed.Orders.Add(new Order
        {
            Id = orderId,
            UserId = actor,
            AccountId = accountId ?? _accountId,
            Instrument = $"CON.F.US.{symbol ?? "X"}.U26",
            Symbol = symbol,
            Side = side,
            Size = size,
            Type = OrderType.Market,
            Status = OrderStatus.Working,
            Mode = TradingMode.Practice,
            PlacedAt = at,
        });
        seed.Fills.Add(new Fill
        {
            Id = Guid.NewGuid(),
            UserId = actor,
            OrderId = orderId,
            VenueFillKey = $"F-{symbol}-{at:HHmm}",
            Price = price,
            Size = size,
            Fees = 1.25m,
            ExecutedAt = at,
        });
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnTheInstrumentsFillsInTheWindow_AscendingWithSideAndPrice()
    {
        await SeedAccountAsync();
        await SeedFillAsync("ES", 5305m, 1, At(10, 30), OrderSide.Sell);
        await SeedFillAsync("ES", 5300m, 2, At(10, 0), OrderSide.Buy);

        FillJournalRead? result = await new FillJournalReadService(Context())
            .ReadAsync(_accountId, Es, At(9), At(11), CancellationToken.None);

        result.Should().NotBeNull();
        result!.WindowTooWide.Should().BeFalse();
        result.Fills.Should().HaveCount(2);
        // Ascending by execution time: the 10:00 Buy 2 before the 10:30 Sell 1.
        result.Fills[0].Should().BeEquivalentTo(new
        {
            Side = "Buy",
            Price = 5300m,
            Size = 2,
            ExecutedAt = At(10, 0),
        });
        result.Fills[1].Side.Should().Be("Sell");
        result.Fills[1].Price.Should().Be(5305m);
    }

    [Fact]
    public async Task ReadAsync_ShouldExcludeAnotherInstrumentsFills()
    {
        await SeedAccountAsync();
        await SeedFillAsync("ES", 5300m, 1, At(10, 0));
        await SeedFillAsync("NQ", 19900m, 1, At(10, 5)); // a different instrument on the same account

        FillJournalRead? result = await new FillJournalReadService(Context())
            .ReadAsync(_accountId, Es, At(9), At(11), CancellationToken.None);

        result!.Fills.Should().ContainSingle().Which.Price.Should().Be(5300m);
    }

    [Fact]
    public async Task ReadAsync_ShouldExcludeFillsOutsideTheHalfOpenWindow()
    {
        await SeedAccountAsync();
        await SeedFillAsync("ES", 1m, 1, At(8, 59)); // before `from`
        await SeedFillAsync("ES", 2m, 1, At(9, 0)); // exactly `from` -- inclusive
        await SeedFillAsync("ES", 3m, 1, At(11, 0)); // exactly `to` -- exclusive

        FillJournalRead? result = await new FillJournalReadService(Context())
            .ReadAsync(_accountId, Es, At(9), At(11), CancellationToken.None);

        result!.Fills.Should().ContainSingle().Which.Price.Should().Be(2m);
    }

    [Fact]
    public async Task ReadAsync_ShouldExcludeAFillWhoseOrderHasNoSymbol()
    {
        // A fill whose order recorded no venue-neutral symbol cannot be attributed to a charted instrument.
        await SeedAccountAsync();
        await SeedFillAsync(null, 5300m, 1, At(10, 0));

        FillJournalRead? result = await new FillJournalReadService(Context())
            .ReadAsync(_accountId, Es, At(9), At(11), CancellationToken.None);

        result!.Fills.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_ShouldBeNull_WhenTheAccountIsNotOwnedByTheCaller()
    {
        // R-20 default-deny: another operator's account is not found, so the endpoint maps this to 404.
        await SeedAccountAsync();
        await SeedFillAsync("ES", 5300m, 1, At(10, 0));

        FillJournalRead? result = await new FillJournalReadService(Context(user: Guid.NewGuid()))
            .ReadAsync(_accountId, Es, At(9), At(11), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_ShouldRefuseAnOverWideWindow_RatherThanTruncate()
    {
        await SeedAccountAsync();
        await using (TradingCopilotDbContext seed = Context())
        {
            for (int i = 0; i <= FillJournalReadService.MaxFills; i++) // MaxFills + 1 fills, all inside the window
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

        FillJournalRead? result = await new FillJournalReadService(Context())
            .ReadAsync(_accountId, Es, At(9), At(11), CancellationToken.None);

        result!.WindowTooWide.Should().BeTrue();
        result.Fills.Should().BeEmpty("a too-wide window is refused, never truncated (the gh#644 discipline)");
    }
}
