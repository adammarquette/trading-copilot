using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Accounts;

/// <summary>
/// The production Trade writer (gh#731): a round trip closing at the venue becomes a journaled <see cref="Trade"/>
/// with a signed, tick-value-aware <c>RealizedPnL</c>. The behaviours that matter: the P&amp;L is right for both
/// sides, a replayed flat event never double-writes, mode comes from the order rather than the account's current
/// declaration, and an unbalanced or foreign position writes nothing at all.
/// </summary>
public class TradeJournalServiceTests
{
    private const string OurCredentialKey = "topstep-main";
    private const string Contract = "CON.F.US.MES.U26";
    private static VenueId Projectx { get; } = VenueId.Parse("projectx");
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly DateTimeOffset _now = new(2026, 1, 15, 14, 30, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private DbContextOptions<TradingCopilotDbContext> Options() =>
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options;

    private TradingCopilotDbContext Context() => new(Options(), new FixedUser(Guid.Empty));

    private TradeJournalService Service() => new(
        Context(),
        Options(),
        Microsoft.Extensions.Options.Options.Create(new ProjectXConnectionOptions { CredentialKey = OurCredentialKey }),
        NullLogger<TradeJournalService>.Instance);

    private PositionEvent Flat(string venueAccountKey, string contract = Contract) =>
        new(VenueAccountId.Create(Projectx, venueAccountKey), _now,
            VenueContractId.Create(Projectx, contract), NetQuantity: 0, new Price(5_300m));

    private async Task<Guid> SeedAccountAsync(
        Guid owner, string venueAccountKey, string credentialKey = OurCredentialKey,
        TradingMode mode = TradingMode.Practice)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = "Topstep", Type = FirmType.PropFirm });
        context.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = owner,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = credentialKey,
        });
        context.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = owner,
            ConnectionId = connectionId,
            VenueAccountKey = venueAccountKey,
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = mode,
            CanTrade = true,
            IsVisible = true,
            Balance = 50_000m,
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    /// <summary>Seeds a filled order and its fills. Returns the order id.</summary>
    private async Task<Guid> SeedFilledOrderAsync(
        Guid owner,
        Guid accountId,
        OrderSide side,
        (decimal Price, int Size, int Minute)[] fills,
        decimal pointValue = 5m,
        TradingMode mode = TradingMode.Practice,
        Guid? suggestionId = null)
    {
        Guid orderId = Guid.NewGuid();
        await using TradingCopilotDbContext context = Context();
        context.Orders.Add(new Order
        {
            Id = orderId,
            UserId = owner,
            AccountId = accountId,
            SuggestionId = suggestionId,
            Instrument = Contract,
            Side = side,
            Size = fills.Sum(fill => fill.Size),
            Type = OrderType.Market,
            EntryPrice = fills[0].Price,
            WorkingStopPrice = 5_290m,
            SafetyStopPrice = 5_280m,
            PointValue = pointValue,
            Status = OrderStatus.Filled,
            Mode = mode,
            VenueOrderKey = $"venue-{orderId:N}",
            PlacedAt = _now,
        });
        foreach ((decimal price, int size, int minute) in fills)
        {
            context.Fills.Add(new Fill
            {
                Id = Guid.NewGuid(),
                UserId = owner,
                OrderId = orderId,
                VenueFillKey = Guid.NewGuid().ToString("N"),
                Price = price,
                Size = size,
                ExecutedAt = _now.AddMinutes(minute),
            });
        }

        await context.SaveChangesAsync();
        return orderId;
    }

    private async Task<List<Trade>> TradesAsync() =>
        await Context().Trades.IgnoreQueryFilters().ToListAsync();

    [Fact]
    public async Task ProcessFlatAsync_ShouldJournalTheRoundTrip_WhenALongClosesForAProfit()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 2, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 2, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        List<Trade> trades = await TradesAsync();
        trades.Should().ContainSingle();
        Trade trade = trades[0];
        trade.Side.Should().Be(OrderSide.Buy);
        trade.EntryPrice.Should().Be(5_000m);
        trade.ExitPrice.Should().Be(5_010m);
        trade.Size.Should().Be(2);
        // 10 points * 2 contracts * $5 point value = $100.
        trade.RealizedPnL.Should().Be(100m);
        trade.ClosedAt.Should().Be(_now.AddMinutes(5));
        trade.AccountId.Should().Be(accountId);
        trade.UserId.Should().Be(owner);
        trade.ClosingFillId.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldSignThePnLByTheEntrySide_WhenAShortClosesForAProfit()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        Trade trade = (await TradesAsync()).Should().ContainSingle().Subject;
        trade.Side.Should().Be(OrderSide.Sell);
        // A short that bought back 10 points lower profits: 10 * 1 * $5 = $50.
        trade.RealizedPnL.Should().Be(50m);
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldRecordALoss_WhenTheLongExitsBelowItsEntry()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(4_990m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().ContainSingle().Subject.RealizedPnL.Should().Be(-50m);
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteExactlyOneTrade_WhenTheFlatEventIsReplayed()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);
        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        // The closing fill is the natural key -- a replay recomposes the same round trip and must not
        // double-count the day's realized P&L into the daily governor.
        (await TradesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldTakeTheModeFromTheOrder_NotTheAccountsCurrentDeclaration()
    {
        Guid owner = Guid.NewGuid();
        // The account now reads Live, but the round trip was placed under Practice -- the journal records the
        // mode of PLACEMENT, so practice results can never blend into live results (R-14).
        Guid accountId = await SeedAccountAsync(owner, "9001", mode: TradingMode.Live);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)], mode: TradingMode.Practice);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)], mode: TradingMode.Practice);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().ContainSingle().Subject.Mode.Should().Be(TradingMode.Practice);
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldCarryTheOriginatingSuggestion_WhenTheEntryCameFromOne()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        Guid suggestionId = Guid.NewGuid();
        await using (TradingCopilotDbContext seed = Context())
        {
            seed.Suggestions.Add(new Suggestion
            {
                Id = suggestionId,
                UserId = owner,
                AccountId = accountId,
                Instrument = Contract,
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = 5_000m,
                StopPrice = 4_990m,
                TargetPrice = 5_010m,
                Mode = TradingMode.Practice,
                State = SuggestionState.Active,
                Rationale = "confluence",
                CitedIndicator = "EMA",
                CitedPeriod = 20,
                CitedResolutionMinutes = 5,
                Confidence = 70,
                CreatedAt = _now,
                ExpiresAt = _now.AddMinutes(15),
            });
            await seed.SaveChangesAsync();
        }

        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)], suggestionId: suggestionId);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().ContainSingle().Subject.SuggestionId.Should().Be(suggestionId);
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteNothing_WhenTheLegsDoNotBalance()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        // 3 in, 2 out -- a partial exit. Not the foundational round trip; refused rather than mis-journalled.
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 3, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 2, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteNothing_WhenThePositionIsNotFlat()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        PositionEvent stillOpen = Flat("9001") with { NetQuantity = 1 };
        await Service().ProcessFlatAsync(stillOpen, CancellationToken.None);

        (await TradesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteNothing_WhenTheAccountBelongsToAnotherCredentialSet()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001", credentialKey: "someone-elses");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldWriteNothing_WhenNoOrderOfOursIsJournalledOnTheContract()
    {
        Guid owner = Guid.NewGuid();
        await SeedAccountAsync(owner, "9001");

        await Service().ProcessFlatAsync(Flat("9001"), CancellationToken.None);

        (await TradesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldOnlyConsiderTheFlatContract_WhenTheAccountTradedAnother()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountAsync(owner, "9001");
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Buy, [(5_000m, 1, 0)]);
        await SeedFilledOrderAsync(owner, accountId, OrderSide.Sell, [(5_010m, 1, 5)]);

        // A different contract goes flat -- the MES round trip above must not be journalled by it.
        await Service().ProcessFlatAsync(Flat("9001", "CON.F.US.NQ.U26"), CancellationToken.None);

        (await TradesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessFlatAsync_ShouldThrow_WhenTheEventIsNull()
    {
        Func<Task> act = () => Service().ProcessFlatAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
