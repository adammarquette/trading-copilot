using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Recovery;

/// <summary>
/// The <c>GET /accounts/{id}/orders</c> shape (gh#381): what the venue-truth resting-orders read actually hands
/// a caller.
/// </summary>
/// <remarks>
/// The response is the contract an operator UI and a staging gate both read, so the two things that must not
/// drift are the <b>size</b> (the field the read exists for) and the <b>404 on an unowned account</b> (R-20
/// default-deny — existence itself is not disclosed).
/// </remarks>
public class WorkingOrderReconciliationEndpointsTests
{
    private static VenueId Venue => VenueId.Parse("projectx");

    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private WorkingOrderReconciliationService Service(IProjectXVenueFactory factory) =>
        new(new TradingCopilotDbContext(
                new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
                new FixedUser(Guid.NewGuid())),
            factory,
            Options.Create(new ProjectXConnectionOptions { CredentialKey = "topstep-main" }),
            Options.Create(new FlattenOptions()),
            NullLogger<WorkingOrderReconciliationService>.Instance);

    [Fact]
    public async Task ReadAsync_ShouldBe404_WhenTheAccountIsNotFoundOrNotOwned()
    {
        // R-20 default-deny at the boundary: another operator's account is indistinguishable from one that does
        // not exist. Anything other than 404 here would disclose existence.
        IResult result = await WorkingOrderReconciliationEndpoints.ReadAsync(
            Guid.NewGuid(), instrument: null, Service(A.Fake<IProjectXVenueFactory>()), CancellationToken.None);

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task ReadAsync_ShouldBe400_WhenTheInstrumentIsBlank()
    {
        // `?instrument=` scopes the read to one contract (gh#772); a blank symbol is a malformed request worth naming,
        // rejected before the venue is asked. (A non-blank but unresolvable symbol is the venue's concern — that is a
        // declared-unknown read, not a 400.)
        IResult result = await WorkingOrderReconciliationEndpoints.ReadAsync(
            Guid.NewGuid(), "   ", Service(A.Fake<IProjectXVenueFactory>()), CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void RestingOrder_ShouldMarkAStopLegProtective_AndALimitLegNot()
    {
        // Surfaced explicitly so a caller does not re-derive the rule. A take-profit rests on the WINNING side —
        // it caps the gain and does nothing about the loss, so it is not protection.
        WorkingOrder stop = new("ORD-1", VenueContractId.Create(Venue, "MESU26"), new Price(4_990m), null, 2);
        WorkingOrder takeProfit = new("ORD-2", VenueContractId.Create(Venue, "MESU26"), null, new Price(5_050m), 2);

        (stop.StopPrice is not null).Should().BeTrue();
        (takeProfit.StopPrice is not null).Should().BeFalse();
    }

    [Fact]
    public void RestingOrdersResponse_ShouldCarryTheBasisAndEachOrdersSize()
    {
        RestingOrdersResponse response = new(
            PositionMarkBasis.Live.ToString(),
            [new RestingOrder("ORD-1", "MESU26", 4_990m, null, 2, true)]);

        response.MarkBasis.Should().Be("Live");
        response.Orders[0].Size.Should().Be(2, "the size is the field this whole read exists to surface");
        response.Orders[0].IsProtective.Should().BeTrue();
    }
}
