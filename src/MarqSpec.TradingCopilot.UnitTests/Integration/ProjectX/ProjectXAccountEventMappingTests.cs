using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.ProjectX;

/// <summary>
/// The gateway → venue-neutral account-event translation (gh#219): the account-event seam's boundary, where the
/// gateway's user-hub payloads become neutral <see cref="AccountEvent"/>s. Nothing venue-specific — bid/ask sides,
/// integer account ids, the gateway's own status vocabulary, the trade handle as the idempotency key — may leak
/// past here.
/// </summary>
public class ProjectXAccountEventMappingTests
{
    private static VenueId Venue { get; } = VenueId.Parse("projectx");
    private static DateTime When { get; } = new(2026, 1, 15, 14, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ClientModels.OrderSide.Bid, OrderSide.Buy)]
    [InlineData(ClientModels.OrderSide.Ask, OrderSide.Sell)]
    public void ToVenueSide_ShouldMapBidAskOntoBuySell(ClientModels.OrderSide vendor, OrderSide expected) =>
        ProjectXMapping.ToVenueSide(vendor).Should().Be(expected);

    [Theory]
    [InlineData(ClientModels.OrderStatus.Open, VenueOrderState.Working)]
    [InlineData(ClientModels.OrderStatus.Filled, VenueOrderState.Filled)]
    [InlineData(ClientModels.OrderStatus.Cancelled, VenueOrderState.Cancelled)]
    [InlineData(ClientModels.OrderStatus.Expired, VenueOrderState.Expired)]
    [InlineData(ClientModels.OrderStatus.Rejected, VenueOrderState.Rejected)]
    [InlineData(ClientModels.OrderStatus.Pending, VenueOrderState.Pending)]
    public void ToVenueOrderState_ShouldMapEachGatewayStatus(ClientModels.OrderStatus vendor, VenueOrderState expected) =>
        ProjectXMapping.ToVenueOrderState(vendor).Should().Be(expected);

    [Fact]
    public void ToVenueOrderState_ShouldFailClosed_ForAnUnmappedStatus() =>
        ProjectXMapping.ToVenueOrderState(ClientModels.OrderStatus.None).Should().Be(VenueOrderState.Unknown);

    [Fact]
    public void ToOrderStateEvent_ShouldCarryTheHandleAccountStatusAndFill()
    {
        ClientModels.OrderUpdate update = new()
        {
            Id = 55_001,
            AccountId = 9001,
            ContractId = "CON.F.US.MES.U26",
            Status = ClientModels.OrderStatus.Open,
            Side = ClientModels.OrderSide.Bid,
            Size = 3,
            FillVolume = 1,
            FilledPrice = 5_300.25m,
            CreationTimestamp = When,
            UpdateTimestamp = When.AddSeconds(2),
        };

        OrderStateEvent result = ProjectXMapping.ToOrderStateEvent(update, Venue);

        result.VenueOrderKey.Should().Be("55001");
        result.Account.Should().Be(VenueAccountId.Create(Venue, "9001"));
        result.State.Should().Be(VenueOrderState.Working);
        result.FilledQuantity.Should().Be(1);
        result.AverageFillPrice.Should().Be(new Price(5_300.25m));
        result.At.Should().Be(new DateTimeOffset(When.AddSeconds(2), TimeSpan.Zero)); // the update time, not creation
    }

    [Fact]
    public void ToOrderStateEvent_ShouldFallBackToCreationTime_WhenNoUpdateTimestamp()
    {
        ClientModels.OrderUpdate update = new()
        {
            Id = 1,
            AccountId = 9001,
            Status = ClientModels.OrderStatus.Rejected,
            CreationTimestamp = When,
            UpdateTimestamp = null,
        };

        ProjectXMapping.ToOrderStateEvent(update, Venue).At.Should().Be(new DateTimeOffset(When, TimeSpan.Zero));
    }

    [Fact]
    public void ToFillEvent_ShouldCarryTheTradeHandleAsTheIdempotencyKey()
    {
        ClientModels.TradeNotification trade = new()
        {
            Id = 88_123,
            OrderId = 55_001,
            AccountId = 9001,
            ContractId = "CON.F.US.MES.U26",
            Price = 5_300.50m,
            Fees = 1.24m,
            Side = ClientModels.OrderSide.Ask,
            Size = 2,
            Voided = false,
            CreationTimestamp = When,
        };

        FillEvent result = ProjectXMapping.ToFillEvent(trade, Venue);

        result.VenueFillKey.Should().Be("88123");   // the trade id -- the natural idempotency key
        result.VenueOrderKey.Should().Be("55001");  // the order the fill is against
        result.Account.Should().Be(VenueAccountId.Create(Venue, "9001"));
        result.Side.Should().Be(OrderSide.Sell);
        result.Quantity.Should().Be(2);
        result.ExecutionPrice.Should().Be(new Price(5_300.50m));
        result.Fees.Should().Be(1.24m);
        result.Voided.Should().BeFalse();
        result.At.Should().Be(new DateTimeOffset(When, TimeSpan.Zero));
    }

    [Fact]
    public void ToFillEvent_ShouldPreserveTheVoidedFlag()
    {
        ClientModels.TradeNotification trade = new() { Id = 1, OrderId = 2, AccountId = 9001, Voided = true, CreationTimestamp = When };

        ProjectXMapping.ToFillEvent(trade, Venue).Voided.Should().BeTrue();
    }

    [Theory]
    [InlineData(ClientModels.PositionType.Long, 4, 4)]
    [InlineData(ClientModels.PositionType.Short, 4, -4)]
    [InlineData(ClientModels.PositionType.Undefined, 0, 0)]
    public void ToPositionEvent_ShouldSignTheNetExposure(ClientModels.PositionType type, int size, int expected)
    {
        ClientModels.PositionUpdate update = new()
        {
            Id = 1,
            AccountId = 9001,
            ContractId = "CON.F.US.MES.U26",
            Type = type,
            Size = size,
            AveragePrice = 5_299m,
            CreationTimestamp = When,
        };

        PositionEvent result = ProjectXMapping.ToPositionEvent(update, Venue);

        result.NetQuantity.Should().Be(expected);
        result.Contract.Should().Be(VenueContractId.Create(Venue, "CON.F.US.MES.U26"));
        result.AveragePrice.Should().Be(new Price(5_299m));
    }
}
