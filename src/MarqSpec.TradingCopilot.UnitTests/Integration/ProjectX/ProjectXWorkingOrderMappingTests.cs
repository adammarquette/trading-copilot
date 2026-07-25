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
}
