using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.UnitTests.Domain.Venue.Fakes;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

/// <summary>
/// Proves the R-17 seam itself: a full trading venue implements all three slices, a data-only provider
/// implements only market data, and everything crossing the seam is venue-tagged.
/// </summary>
public class VenueCompositionTests
{
    [Fact]
    public void TradingVenue_ShouldImplementAllThreeSlices()
    {
        ITradingVenue venue = new FakeTradingVenue();

        venue.Should().BeAssignableTo<IMarketDataSource>();
        venue.Should().BeAssignableTo<IAccountSource>();
        venue.Should().BeAssignableTo<IOrderExecutor>();
    }

    [Fact]
    public void DataOnlyProvider_ShouldImplementOnlyTheMarketDataSlice()
    {
        IMarketDataSource feed = new FakeQuoteFeed();

        feed.Should().BeAssignableTo<IMarketDataSource>();
        feed.Should().NotBeAssignableTo<IAccountSource>();
        feed.Should().NotBeAssignableTo<IOrderExecutor>();
        feed.Should().NotBeAssignableTo<ITradingVenue>();
    }

    [Fact]
    public async Task ResolveContractAsync_ShouldTagTheContractWithTheVenue()
    {
        ITradingVenue venue = new FakeTradingVenue();

        ResolvedContract contract = await venue.ResolveContractAsync(InstrumentId.Parse("ES"), CancellationToken.None);

        contract.Contract.Venue.Should().Be(venue.Id);
        contract.Instrument.Should().Be(InstrumentId.Parse("ES"));
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldReturnAnOrderTaggedWithTheAccount()
    {
        FakeTradingVenue venue = new();
        VenueAccountId account = VenueAccountId.Create(venue.Id, "9001");
        OrderRequest request = new(
            account,
            VenueContractId.Create(venue.Id, "ESM25"),
            OrderSide.Buy,
            OrderType.Market,
            Quantity: 1,
            LimitPrice: null,
            StopPrice: null);

        PlacedOrder placed = await venue.PlaceOrderAsync(request, CancellationToken.None);

        placed.Account.Should().Be(account);
        placed.VenueOrderId.Should().NotBeNullOrWhiteSpace();
        venue.PlacedOrders.Should().ContainSingle().Which.Should().Be(request);
    }

    [Fact]
    public async Task ClosePositionAsync_ShouldReturnTheVenuesPostCloseView()
    {
        ITradingVenue venue = new FakeTradingVenue();
        VenueAccountId account = VenueAccountId.Create(venue.Id, "9001");
        VenueContractId contract = VenueContractId.Create(venue.Id, "ESM25");

        PositionSnapshot result = await venue.ClosePositionAsync(account, contract, CancellationToken.None);

        result.IsFlat.Should().BeTrue();
        result.Account.Should().Be(account);
    }

    [Fact]
    public async Task GetBarsAsync_ShouldThrowCapabilityNotSupported_WhenTheProviderLacksHistoricalBars()
    {
        IMarketDataSource feed = new FakeQuoteFeed();

        Func<Task> act = async () => await feed.GetBarsAsync(
            VenueContractId.Create(feed.Id, "SPY"),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        await act.Should().ThrowAsync<VenueCapabilityNotSupportedException>();
    }
}
