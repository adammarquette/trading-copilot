using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Tradovate;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tradovate;

/// <summary>
/// The Tradovate → neutral <see cref="AccountEvent"/> translation (R-17, gh#977). The cases that matter are the ones
/// where Tradovate carries <b>less</b> than the neutral model asks for: an order entity with no cumulative filled
/// quantity, a fill with no account and no fee, and an open position with no net price. Each is a chance to fabricate
/// a number on the path that feeds the risk gate, so each has a guard here.
/// </summary>
public class TradovateAccountEventMappingTests
{
    private static VenueId Tradovate { get; } = VenueId.Parse("tradovate");

    private static DateTimeOffset At { get; } = new(2026, 3, 4, 14, 30, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------------------------------------------------
    // Order status
    // ---------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(ClientModels.OrderStatus.Working, VenueOrderState.Working)]
    [InlineData(ClientModels.OrderStatus.PendingCancel, VenueOrderState.Working)]
    [InlineData(ClientModels.OrderStatus.PendingReplace, VenueOrderState.Working)]
    [InlineData(ClientModels.OrderStatus.PendingNew, VenueOrderState.Pending)]
    [InlineData(ClientModels.OrderStatus.Filled, VenueOrderState.Filled)]
    [InlineData(ClientModels.OrderStatus.Canceled, VenueOrderState.Cancelled)]
    [InlineData(ClientModels.OrderStatus.Expired, VenueOrderState.Expired)]
    [InlineData(ClientModels.OrderStatus.Rejected, VenueOrderState.Rejected)]
    public void ToVenueOrderState_ShouldMapTheStatus_WhenTradovateNamesOneTheDomainHas(
        ClientModels.OrderStatus status, VenueOrderState expected)
    {
        TradovateMapping.ToVenueOrderState(status).Should().Be(expected);
    }

    [Theory]
    [InlineData(ClientModels.OrderStatus.Unknown)]
    [InlineData(ClientModels.OrderStatus.Completed)]
    [InlineData(ClientModels.OrderStatus.Suspended)]
    public void ToVenueOrderState_ShouldBeUnknown_ForAStatusThatCannotBeMappedSafely(ClientModels.OrderStatus status)
    {
        // Fail closed (gh#60). `Completed` is NOT read as Filled: whether Tradovate means "fully executed" or merely
        // "finished" is unpinned from this side, and reading a cancel as a fill would tell the journal an execution
        // happened. `Suspended` is live-but-not-executable, which is no neutral state at all.
        TradovateMapping.ToVenueOrderState(status).Should().Be(VenueOrderState.Unknown);
    }

    [Theory]
    [InlineData(ClientModels.OrderAction.Buy, OrderSide.Buy)]
    [InlineData(ClientModels.OrderAction.Sell, OrderSide.Sell)]
    public void ToVenueSide_ShouldMapTheAction(ClientModels.OrderAction action, OrderSide expected)
    {
        TradovateMapping.ToVenueSide(action).Should().Be(expected);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Order → OrderStateEvent
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public void ToOrderStateEvent_ShouldTagTheVenueOnTheAccountAndCarryTheOrderHandle()
    {
        OrderStateEvent result = TradovateMapping.ToOrderStateEvent(Order(id: 5150, account: 9001), Tradovate);

        result.Account.Should().Be(VenueAccountId.Create(Tradovate, "9001"));
        result.VenueOrderKey.Should().Be("5150");
        result.At.Should().Be(At);
        result.State.Should().Be(VenueOrderState.Working);
    }

    [Fact]
    public void ToOrderStateEvent_ShouldReportNoFilledQuantityOrAverageFillPrice_BecauseTradovateCarriesNeither()
    {
        // The Tradovate order entity has no cumulative filled quantity and no average fill price. Reporting 0 and 0
        // would be indistinguishable from "nothing has filled yet" on a field a consumer may read as venue truth, so
        // the absence is reported as an absence. Filled volume comes from the fill events regardless.
        OrderStateEvent result = TradovateMapping.ToOrderStateEvent(Order(id: 5150, account: 9001), Tradovate);

        result.FilledQuantity.Should().BeNull();
        result.AverageFillPrice.Should().BeNull();
    }

    [Fact]
    public void ToOrderStateEvent_ShouldThrow_WhenTheOrderHasNoId()
    {
        // The id is the handle the journal resolves the order by. Without it the event names nothing.
        Action map = () => TradovateMapping.ToOrderStateEvent(Order(id: null, account: 9001), Tradovate);

        map.Should().Throw<TradovateVenueException>();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Fill → FillEvent
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public void ToFillEvent_ShouldCarryTheResolvedAccountAndBothVenueKeys()
    {
        VenueAccountId account = VenueAccountId.Create(Tradovate, "9001");

        FillEvent result = TradovateMapping.ToFillEvent(Fill(id: 77, order: 5150), account, Tradovate);

        result.Account.Should().Be(account);
        result.VenueOrderKey.Should().Be("5150");
        result.VenueFillKey.Should().Be("77");
        result.Side.Should().Be(OrderSide.Buy);
        result.Quantity.Should().Be(2);
        result.ExecutionPrice.Should().Be(new Price(5312.25m));
        result.At.Should().Be(At);
    }

    [Fact]
    public void ToFillEvent_ShouldReportZeroFees_BecauseTradovateReportsNoneOnTheFill()
    {
        // Documented gap, not an omission: the Tradovate fill entity carries no commission, and the neutral FillEvent
        // has no way to say "unknown". A zero UNDER-states cost, so realized P&L reads slightly generous — followed
        // up separately rather than papered over with an invented number.
        TradovateMapping.ToFillEvent(Fill(id: 77, order: 5150), VenueAccountId.Create(Tradovate, "9001"), Tradovate)
            .Fees.Should().Be(0m);
    }

    [Fact]
    public void ToFillEvent_ShouldMarkItVoided_WhenTradovateReportsTheFillInactive()
    {
        FillEvent result = TradovateMapping.ToFillEvent(
            Fill(id: 77, order: 5150) with { Active = false }, VenueAccountId.Create(Tradovate, "9001"), Tradovate);

        result.Voided.Should().BeTrue();
    }

    [Fact]
    public void ToFillEvent_ShouldNotMarkItVoided_WhenTradovateReportsTheFillActive()
    {
        TradovateMapping.ToFillEvent(Fill(id: 77, order: 5150), VenueAccountId.Create(Tradovate, "9001"), Tradovate)
            .Voided.Should().BeFalse();
    }

    [Fact]
    public void ToFillEvent_ShouldMapASell()
    {
        FillEvent result = TradovateMapping.ToFillEvent(
            Fill(id: 77, order: 5150) with { Action = ClientModels.OrderAction.Sell },
            VenueAccountId.Create(Tradovate, "9001"),
            Tradovate);

        result.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public void ToFillEvent_ShouldThrow_WhenTheFillHasNoId()
    {
        // The fill id is the idempotency key the persistence layer dedupes on. Without it a replay double-counts.
        Action map = () => TradovateMapping.ToFillEvent(
            Fill(id: null, order: 5150), VenueAccountId.Create(Tradovate, "9001"), Tradovate);

        map.Should().Throw<TradovateVenueException>();
    }

    [Fact]
    public void ToFillEvent_ShouldThrow_WhenTheAccountBelongsToAnotherVenue()
    {
        // Tradovate account handles are bare integers that collide freely with ProjectX's, so a foreign handle must
        // never be stamped onto a Tradovate execution — that would journal the fill against somebody else's account.
        Action map = () => TradovateMapping.ToFillEvent(
            Fill(id: 77, order: 5150), VenueAccountId.Create(VenueId.Parse("projectx"), "9001"), Tradovate);

        map.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Position → PositionEvent
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public void ToPositionEvent_ShouldKeepTheSign_WhenTheNetPositionIsShort()
    {
        PositionEvent result = TradovateMapping.ToPositionEvent(Position(net: -3, price: 5310m), Tradovate);

        result.NetQuantity.Should().Be(-3);
        result.Account.Should().Be(VenueAccountId.Create(Tradovate, "9001"));
        result.Contract.Should().Be(VenueContractId.Create(Tradovate, "222"));
        result.AveragePrice.Should().Be(new Price(5310m));
    }

    [Fact]
    public void ToPositionEvent_ShouldMapAFlatPosition_EvenWithNoNetPrice()
    {
        // The flat is the OCO-cancel-on-exit trigger (gh#183) — the one position event that retires live protection —
        // and a flat position's price is immaterial. Refusing it for a missing price would leave a resting safety stop
        // behind a position that no longer exists.
        PositionEvent result = TradovateMapping.ToPositionEvent(Position(net: 0, price: null), Tradovate);

        result.NetQuantity.Should().Be(0);
        result.AveragePrice.Should().Be(new Price(0m));
    }

    [Fact]
    public void ToPositionEvent_ShouldThrow_WhenAnOpenPositionHasNoNetPrice()
    {
        // Absent ≠ zero. A fabricated 0 basis on an OPEN position feeds a wildly wrong unrealised P&L to the R-5 gate.
        Action map = () => TradovateMapping.ToPositionEvent(Position(net: 2, price: null), Tradovate);

        map.Should().Throw<TradovateVenueException>();
    }

    private static ClientModels.Order Order(long? id, long account) => new()
    {
        Id = id,
        AccountId = account,
        ContractId = 222,
        Timestamp = At,
        Action = ClientModels.OrderAction.Buy,
        OrdStatus = ClientModels.OrderStatus.Working,
        Admin = false,
    };

    private static ClientModels.Fill Fill(long? id, long order) => new()
    {
        Id = id,
        OrderId = order,
        ContractId = 222,
        Timestamp = At,
        TradeDate = new ClientModels.TradeDate { Year = 2026, Month = 3, Day = 4 },
        Action = ClientModels.OrderAction.Buy,
        Qty = 2,
        Price = 5312.25m,
        Active = true,
        FinallyPaired = 0,
    };

    private static ClientModels.Position Position(int net, decimal? price) => new()
    {
        Id = 11,
        AccountId = 9001,
        ContractId = 222,
        Timestamp = At,
        TradeDate = new ClientModels.TradeDate { Year = 2026, Month = 3, Day = 4 },
        NetPos = net,
        NetPrice = price,
        Bought = 2,
        BoughtValue = 10624.50m,
        Sold = 0,
        SoldValue = 0m,
        PrevPos = 0,
    };
}
