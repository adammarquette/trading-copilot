using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Execution;

/// <summary>
/// The pure OCO-cancel-on-exit selection (gh#183): on a flat contract, the venue-resting legs to cancel are those
/// <b>not backed by a journaled entry</b>. A protective leg the venue spawned (safety bracket, promoted stop) is
/// never a journaled order and is cancelled; the operator's journaled resting entry is preserved; only the flat
/// contract's legs are touched.
/// </summary>
public class OcoExitSelectionTests
{
    private static VenueId Venue { get; } = VenueId.Parse("projectx");
    private static VenueContractId Mes { get; } = VenueContractId.Create(Venue, "CON.F.US.MES.U26");
    private static VenueContractId Es { get; } = VenueContractId.Create(Venue, "CON.F.US.EP.U26");

    private static WorkingOrder Order(string key, VenueContractId contract, decimal? stop = null, decimal? limit = null) =>
        new(key, contract, stop is { } s ? new Price(s) : null, limit is { } l ? new Price(l) : null, Size: 1);

    [Fact]
    public void LegsToCancel_ShouldCancelTheUnjournaledProtectiveLegs_OnTheFlatContract()
    {
        WorkingOrder safetyStop = Order("leg-safety", Mes, stop: 5_280m);
        WorkingOrder promotedStop = Order("leg-actual", Mes, stop: 5_290m);
        WorkingOrder takeProfit = Order("leg-target", Mes, limit: 5_360m);
        WorkingOrder restingEntry = Order("entry-1", Mes, stop: 5_320m);

        IReadOnlyList<WorkingOrder> cancel = OcoExitSelection.LegsToCancel(
            [safetyStop, promotedStop, takeProfit, restingEntry],
            Mes,
            new HashSet<string> { "entry-1" }); // the journaled resting entry

        cancel.Select(order => order.VenueOrderKey)
            .Should().BeEquivalentTo(["leg-safety", "leg-actual", "leg-target"]); // the three unjournaled legs
    }

    [Fact]
    public void LegsToCancel_ShouldPreserveAJournaledRestingEntry()
    {
        IReadOnlyList<WorkingOrder> cancel = OcoExitSelection.LegsToCancel(
            [Order("entry-1", Mes, stop: 5_320m)],
            Mes,
            new HashSet<string> { "entry-1" });

        cancel.Should().BeEmpty(); // the operator's own pending order is never cancelled
    }

    [Fact]
    public void LegsToCancel_ShouldIgnoreLegsOnOtherContracts()
    {
        WorkingOrder onEs = Order("leg-es", Es, stop: 4_400m);

        IReadOnlyList<WorkingOrder> cancel = OcoExitSelection.LegsToCancel(
            [onEs, Order("leg-mes", Mes, stop: 5_280m)],
            Mes,
            new HashSet<string>());

        cancel.Should().ContainSingle().Which.VenueOrderKey.Should().Be("leg-mes"); // only the flat contract's leg
    }

    [Fact]
    public void LegsToCancel_ShouldReturnEmpty_WhenNothingIsResting()
    {
        OcoExitSelection.LegsToCancel([], Mes, new HashSet<string>()).Should().BeEmpty();
    }
}
