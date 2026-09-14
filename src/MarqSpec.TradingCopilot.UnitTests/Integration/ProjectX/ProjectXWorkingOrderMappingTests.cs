using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.ProjectX;

/// <summary>
/// The gateway open-order → venue-neutral <see cref="WorkingOrder"/> translation (gh#183): the handle, contract,
/// and resting price the OCO-cancel-on-exit pass needs, with nothing venue-specific leaking past the boundary.
/// </summary>
public class ProjectXWorkingOrderMappingTests
{
    private static VenueId Venue { get; } = VenueId.Parse("projectx");

    [Fact]
    public void ToWorkingOrder_ShouldCarryTheHandleContractAndStopPrice()
    {
        ClientModels.Order order = new()
        {
            Id = 55_001,
            AccountId = 9001,
            ContractId = "CON.F.US.MES.U26",
            Status = ClientModels.OrderStatus.Open,
            Type = ClientModels.OrderType.Stop,
            StopPrice = 5_280m,
        };

        WorkingOrder result = ProjectXMapping.ToWorkingOrder(order, Venue);

        result.VenueOrderKey.Should().Be("55001");
        result.Contract.Should().Be(VenueContractId.Create(Venue, "CON.F.US.MES.U26"));
        result.StopPrice.Should().Be(new Price(5_280m));
        result.LimitPrice.Should().BeNull();
    }

    [Fact]
    public void ToWorkingOrder_ShouldCarryALimitPrice_ForATakeProfitLeg()
    {
        ClientModels.Order order = new()
        {
            Id = 55_002,
            AccountId = 9001,
            ContractId = "CON.F.US.MES.U26",
            Status = ClientModels.OrderStatus.Open,
            Type = ClientModels.OrderType.Limit,
            LimitPrice = 5_360m,
        };

        WorkingOrder result = ProjectXMapping.ToWorkingOrder(order, Venue);

        result.LimitPrice.Should().Be(new Price(5_360m));
        result.StopPrice.Should().BeNull();
    }

    [Fact]
    public void ToWorkingOrder_ShouldCarryTheOrderSize()
    {
        // gh#381. The gateway has always carried Size; the projection dropped it, so a protective leg sized to
        // LESS than the position it guards was invisible through the app -- and a partially-covered position is
        // not a protected one.
        ClientModels.Order order = new()
        {
            Id = 55_003,
            AccountId = 9001,
            ContractId = "CON.F.US.MES.U26",
            Status = ClientModels.OrderStatus.Open,
            Type = ClientModels.OrderType.Stop,
            StopPrice = 5_280m,
            Size = 3,
        };

        ProjectXMapping.ToWorkingOrder(order, Venue).Size.Should().Be(3);
    }

    [Fact]
    public void ToWorkingOrder_ShouldCarryTheCustomTag_SoAReplayCanMatchItsOwnOrder()
    {
        // gh#577 — the correlation handle the placing request stamped, echoed back by the gateway on the
        // resting-orders read. It is how a replay recognises its OWN already-placed order (a conditional left live by
        // a transmit→journal fault matches on its firing conditional's id) rather than transmitting a duplicate.
        ClientModels.Order order = new()
        {
            Id = 55_004,
            AccountId = 9001,
            ContractId = "CON.F.US.MES.U26",
            Status = ClientModels.OrderStatus.Open,
            Type = ClientModels.OrderType.Stop,
            StopPrice = 5_280m,
            CustomTag = "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
        };

        ProjectXMapping.ToWorkingOrder(order, Venue).CustomTag.Should().Be("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
    }

    [Fact]
    public void ToWorkingOrder_ShouldCarryANullCustomTag_ForALegTheVenueSpawned()
    {
        // A protective bracket leg the gateway spawned itself carries no client tag — null, so the OCO-exit
        // "journaled vs unjournaled" distinction (gh#183) stays clean rather than matching an empty string.
        ClientModels.Order order = new()
        {
            Id = 55_005,
            AccountId = 9001,
            ContractId = "CON.F.US.MES.U26",
            Status = ClientModels.OrderStatus.Open,
            Type = ClientModels.OrderType.Stop,
            StopPrice = 5_280m,
        };

        ProjectXMapping.ToWorkingOrder(order, Venue).CustomTag.Should().BeNull();
    }
}
