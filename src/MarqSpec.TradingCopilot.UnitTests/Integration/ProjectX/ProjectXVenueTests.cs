using FakeItEasy;
using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.WebSocket;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.ProjectX;
using ClientModels = MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.ProjectX;

/// <summary>
/// The adapter behind <see cref="ITradingVenue"/>. The cases that matter most are the ones where ProjectX reports
/// failure without throwing — treating those as success would mean believing an order was placed when it was not.
/// </summary>
public class ProjectXVenueTests
{
    private const string ContractKey = "CON.F.US.EP.M25";

    private readonly IProjectXApiClient _api = A.Fake<IProjectXApiClient>();
    private readonly IProjectXWebSocketClient _webSocket = A.Fake<IProjectXWebSocketClient>();
    private readonly ProjectXVenue _venue;

    public ProjectXVenueTests()
    {
        _venue = new ProjectXVenue(_api, _webSocket, ProjectXDataTier.Simulated);
    }

    private VenueAccountId Account => VenueAccountId.Create(_venue.Id, "9001");

    private VenueContractId Contract => VenueContractId.Create(_venue.Id, ContractKey);

    private OrderRequest MarketBuy(int quantity = 1)
    {
        return new OrderRequest(
            Account,
            Contract,
            OrderSide.Buy,
            OrderType.Market,
            quantity,
            LimitPrice: null,
            StopPrice: null);
    }

    // --- Identity and capabilities ---

    [Fact]
    public void Id_ShouldBeProjectX()
    {
        _venue.Id.Should().Be(VenueId.Parse("projectx"));
    }

    [Fact]
    public void Capabilities_ShouldDeclareWhatTheAdapterCanActuallyDeliver()
    {
        _venue.Capabilities.Supports(VenueCapability.HistoricalBars).Should().BeTrue();
        _venue.Capabilities.Supports(VenueCapability.Quotes).Should().BeTrue();
        _venue.Capabilities.Supports(VenueCapability.ClosePosition).Should().BeTrue();
    }

    [Theory]
    [InlineData(VenueCapability.BracketOrders)]
    [InlineData(VenueCapability.TrailingStops)]
    [InlineData(VenueCapability.ModifyOrder)]
    [InlineData(VenueCapability.MarketDepth)]
    [InlineData(VenueCapability.AccountStreaming)]
    public void Capabilities_ShouldNotAdvertiseWhatTheNeutralContractCannotReach(VenueCapability capability)
    {
        // ProjectX does all of these; ITradingVenue exposes none of them yet. Advertising them would send a
        // caller down a path it cannot take -- the opposite of the graceful degradation the model exists for.
        _venue.Capabilities.Supports(capability).Should().BeFalse();
    }

    // --- The data tier: asking the wrong universe returns nothing, not an error ---

    [Fact]
    public void Constructor_ShouldThrow_WhenTheDataTierIsNotRecognized()
    {
        // Falling through to simulated would silently recreate the empty-universe failure the
        // required parameter exists to prevent.
        Action act = () => new ProjectXVenue(_api, _webSocket, (ProjectXDataTier)99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ResolveContractAsync_ShouldQueryTheSimulatedUniverse_WhenTheTierIsSimulated()
    {
        // Practice credentials against live:true return an EMPTY contract list rather than an error, which
        // surfaces later as "no contract matches ES". Verified against a live practice account.
        A.CallTo(() => _api.SearchContractsAsync("ES", false, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Contract>>(
                [new ClientModels.Contract { Id = ContractKey, ActiveContract = true }]);

        VenueContractId resolved = await _venue.ResolveContractAsync(InstrumentId.Parse("ES"), CancellationToken.None);

        resolved.Key.Should().Be(ContractKey);
        A.CallTo(() => _api.SearchContractsAsync("ES", true, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ResolveContractAsync_ShouldQueryTheLiveUniverse_WhenTheTierIsLive()
    {
        ProjectXVenue live = new(_api, _webSocket, ProjectXDataTier.Live);
        A.CallTo(() => _api.SearchContractsAsync("ES", true, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Contract>>(
                [new ClientModels.Contract { Id = ContractKey, ActiveContract = true }]);

        await live.ResolveContractAsync(InstrumentId.Parse("ES"), CancellationToken.None);

        A.CallTo(() => _api.SearchContractsAsync("ES", false, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task GetBarsAsync_ShouldRequestTheConfiguredTier()
    {
        A.CallTo(() => _api.GetHistoricalBarsAsync(
                A<string>._, A<DateTime>._, A<DateTime>._, A<ClientModels.AggregateBarUnit>._,
                A<int>._, A<int>._, false, A<bool>._, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.AggregateBar>>([]);

        await _venue.GetBarsAsync(
            Contract,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        A.CallTo(() => _api.GetHistoricalBarsAsync(
                A<string>._, A<DateTime>._, A<DateTime>._, A<ClientModels.AggregateBarUnit>._,
                A<int>._, A<int>._, true, A<bool>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- Venue tagging is enforced, not assumed ---

    [Fact]
    public async Task PlaceOrderAsync_ShouldThrow_WhenTheContractBelongsToAnotherVenue()
    {
        OrderRequest foreign = MarketBuy() with
        {
            Contract = VenueContractId.Create(VenueId.Parse("tradovate"), ContractKey),
        };

        Func<Task> act = async () => await _venue.PlaceOrderAsync(foreign, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ClosePositionAsync_ShouldThrow_WhenTheContractBelongsToAnotherVenue()
    {
        // Especially dangerous on the flatten path: a colliding key would close the wrong position.
        VenueContractId foreign = VenueContractId.Create(VenueId.Parse("tradovate"), ContractKey);

        Func<Task> act = async () => await _venue.ClosePositionAsync(Account, foreign, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        A.CallTo(() => _api.ClosePositionAsync(A<int>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldRefuseATrailingStop_WhileTheTicketCarriesNoTrailDistance()
    {
        OrderRequest trailing = MarketBuy() with { Type = OrderType.TrailingStop };

        Func<Task> act = async () => await _venue.PlaceOrderAsync(trailing, CancellationToken.None);

        await act.Should().ThrowAsync<VenueCapabilityNotSupportedException>();
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    // --- Accounts ---

    [Fact]
    public async Task GetAccountsAsync_ShouldReturnEveryAccountIncludingUnselectableOnes()
    {
        A.CallTo(() => _api.GetAccountsAsync(A<bool>._, A<CancellationToken>._)).Returns<IEnumerable<ClientModels.TradingAccount>>(
        [
            new ClientModels.TradingAccount { Id = 9001, Name = "PRAC-50K", CanTrade = true, IsVisible = true, Simulated = true },
            new ClientModels.TradingAccount { Id = 9002, Name = "50KTC-V2", CanTrade = false, IsVisible = true, Simulated = false },
        ]);

        IReadOnlyList<VenueAccount> accounts = await _venue.GetAccountsAsync(CancellationToken.None);

        accounts.Should().HaveCount(2);
        accounts[0].Mode.Should().Be(TradingMode.Practice);
        accounts[1].Mode.Should().Be(TradingMode.Live);
        accounts[1].IsSelectable.Should().BeFalse();
    }

    // --- Contract resolution ---

    [Fact]
    public async Task ResolveContractAsync_ShouldPreferTheActiveContract_WhenSeveralMatch()
    {
        A.CallTo(() => _api.SearchContractsAsync("ES", A<bool>._, A<CancellationToken>._)).Returns<IEnumerable<ClientModels.Contract>>(
        [
            new ClientModels.Contract { Id = "CON.F.US.EP.H25", ActiveContract = false, TickSize = 0.25m, TickValue = 12.5m },
            new ClientModels.Contract { Id = ContractKey, ActiveContract = true, TickSize = 0.25m, TickValue = 12.5m },
        ]);

        VenueContractId resolved = await _venue.ResolveContractAsync(InstrumentId.Parse("ES"), CancellationToken.None);

        resolved.Key.Should().Be(ContractKey);
        resolved.Venue.Should().Be(_venue.Id);
    }

    [Fact]
    public async Task ResolveContractAsync_ShouldThrow_WhenNothingMatchesTheInstrument()
    {
        A.CallTo(() => _api.SearchContractsAsync(A<string>._, A<bool>._, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Contract>>([]);

        Func<Task> act = async () => await _venue.ResolveContractAsync(InstrumentId.Parse("ZZ"), CancellationToken.None);

        await act.Should().ThrowAsync<ProjectXVenueException>();
    }

    // --- Positions ---

    [Fact]
    public async Task GetPositionsAsync_ShouldMapVenuePositions()
    {
        A.CallTo(() => _api.GetOpenPositionsAsync(9001, A<CancellationToken>._)).Returns<IEnumerable<ClientModels.Position>>(
        [
            new ClientModels.Position
            {
                AccountId = 9001,
                ContractId = ContractKey,
                Type = ClientModels.PositionType.Short,
                Size = 2,
                AveragePrice = 5_000m,
            },
        ]);

        IReadOnlyList<PositionSnapshot> positions = await _venue.GetPositionsAsync(Account, CancellationToken.None);

        positions.Should().ContainSingle();
        positions[0].NetQuantity.Should().Be(-2);
    }

    // --- Placing orders: the gateway reports failure in the payload, not by throwing ---

    [Fact]
    public async Task PlaceOrderAsync_ShouldReturnThePlacedOrder_WhenTheGatewayAccepts()
    {
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = 555 });

        PlacedOrder placed = await _venue.PlaceOrderAsync(MarketBuy(), CancellationToken.None);

        placed.VenueOrderId.Should().Be("555");
        placed.Account.Should().Be(Account);
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldThrow_WhenTheGatewayReportsFailure()
    {
        // A rejected order comes back as Success=false with a 200 response. Ignoring that would leave us
        // believing a position exists that does not.
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Returns(new ClientModels.PlaceOrderResponse { Success = false, ErrorCode = 42, ErrorMessage = "Insufficient margin" });

        Func<Task> act = async () => await _venue.PlaceOrderAsync(MarketBuy(), CancellationToken.None);

        await act.Should().ThrowAsync<ProjectXVenueException>().Where(e => e.Message.Contains("Insufficient margin"));
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldThrow_WhenTheGatewayReportsSuccessWithoutAnOrderId()
    {
        // Success with no handle leaves nothing to cancel or flatten against -- refuse rather than carry on.
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = null });

        Func<Task> act = async () => await _venue.PlaceOrderAsync(MarketBuy(), CancellationToken.None);

        await act.Should().ThrowAsync<ProjectXVenueException>();
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldTranslateTheTicketToTheGatewaysVocabulary()
    {
        ClientModels.PlaceOrderRequest? sent = null;
        A.CallTo(() => _api.PlaceOrderAsync(A<ClientModels.PlaceOrderRequest>._, A<CancellationToken>._))
            .Invokes((ClientModels.PlaceOrderRequest r, CancellationToken _) => sent = r)
            .Returns(new ClientModels.PlaceOrderResponse { Success = true, OrderId = 1 });

        await _venue.PlaceOrderAsync(MarketBuy(quantity: 3), CancellationToken.None);

        sent.Should().NotBeNull();
        sent!.AccountId.Should().Be(9001);
        sent.ContractId.Should().Be(ContractKey);
        sent.Side.Should().Be(ClientModels.OrderSide.Bid);
        sent.Type.Should().Be(ClientModels.OrderType.Market);
        sent.Size.Should().Be(3);
    }

    [Fact]
    public async Task CancelOrderAsync_ShouldThrow_WhenTheGatewayReportsFailure()
    {
        A.CallTo(() => _api.CancelOrderAsync(9001, 555, A<CancellationToken>._))
            .Returns(new ClientModels.CancelOrderResponse { Success = false, ErrorMessage = "Unknown order" });

        Func<Task> act = async () => await _venue.CancelOrderAsync(Account, "555", CancellationToken.None);

        await act.Should().ThrowAsync<ProjectXVenueException>();
    }

    // --- Closing a position: the venue's own view is the answer (ADR-0013) ---

    [Fact]
    public async Task ClosePositionAsync_ShouldReportFlat_WhenTheVenueNoLongerHoldsThePosition()
    {
        A.CallTo(() => _api.ClosePositionAsync(9001, ContractKey, A<CancellationToken>._))
            .Returns(new ClientModels.ClosePositionResponse { Success = true });
        A.CallTo(() => _api.GetOpenPositionsAsync(9001, A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.Position>>([]);

        PositionSnapshot result = await _venue.ClosePositionAsync(Account, Contract, CancellationToken.None);

        result.IsFlat.Should().BeTrue();
    }

    [Fact]
    public async Task ClosePositionAsync_ShouldReportWhatRemains_WhenTheVenueStillHoldsThePosition()
    {
        // Never assume a close worked. If size survives, say so -- auto-flatten depends on this being honest.
        A.CallTo(() => _api.ClosePositionAsync(9001, ContractKey, A<CancellationToken>._))
            .Returns(new ClientModels.ClosePositionResponse { Success = true });
        A.CallTo(() => _api.GetOpenPositionsAsync(9001, A<CancellationToken>._)).Returns<IEnumerable<ClientModels.Position>>(
        [
            new ClientModels.Position
            {
                AccountId = 9001,
                ContractId = ContractKey,
                Type = ClientModels.PositionType.Long,
                Size = 1,
                AveragePrice = 5_000m,
            },
        ]);

        PositionSnapshot result = await _venue.ClosePositionAsync(Account, Contract, CancellationToken.None);

        result.IsFlat.Should().BeFalse();
        result.NetQuantity.Should().Be(1);
    }

    [Fact]
    public async Task ClosePositionAsync_ShouldThrow_WhenTheGatewayRefusesTheClose()
    {
        A.CallTo(() => _api.ClosePositionAsync(9001, ContractKey, A<CancellationToken>._))
            .Returns(new ClientModels.ClosePositionResponse { Success = false, ErrorMessage = "Market closed" });

        Func<Task> act = async () => await _venue.ClosePositionAsync(Account, Contract, CancellationToken.None);

        await act.Should().ThrowAsync<ProjectXVenueException>();
    }

    // --- Bars ---

    [Fact]
    public async Task GetBarsAsync_ShouldMapTheGatewaysBars()
    {
        A.CallTo(() => _api.GetHistoricalBarsAsync(
                ContractKey,
                A<DateTime>._,
                A<DateTime>._,
                ClientModels.AggregateBarUnit.Minute,
                5,
                A<int>._,
                A<bool>._,
                A<bool>._,
                A<CancellationToken>._))
            .Returns<IEnumerable<ClientModels.AggregateBar>>(
            [
                new ClientModels.AggregateBar { Timestamp = new DateTime(2026, 7, 20, 14, 30, 0, DateTimeKind.Utc), Open = 5_000m, High = 5_010m, Low = 4_995m, Close = 5_005m, Volume = 900 },
            ]);

        IReadOnlyList<Bar> bars = await _venue.GetBarsAsync(
            Contract,
            new DateTimeOffset(2026, 7, 20, 14, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        bars.Should().ContainSingle();
        bars[0].Close.Should().Be(new Price(5_005m));
    }

    // --- Streaming: two SignalR hubs behind an IAsyncEnumerable ---

    [Fact]
    public void StreamQuotesAsync_ShouldNotSubscribe_WhenTheSequenceIsNeverEnumerated()
    {
        // Registration lives inside the iterator, so an unconsumed stream leaves no handler or subscription
        // behind to accumulate ticks for the life of the process.
        _ = _venue.StreamQuotesAsync(Contract, CancellationToken.None);

        A.CallTo(() => _webSocket.SubscribeToPriceUpdatesAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void StreamQuotesAsync_ShouldThrow_WhenTheContractBelongsToAnotherVenue()
    {
        VenueContractId foreign = VenueContractId.Create(VenueId.Parse("tradovate"), ContractKey);

        Action act = () => _venue.StreamQuotesAsync(foreign, CancellationToken.None);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldYieldMappedQuotes_WhenTheMarketHubPublishes()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        TaskCompletionSource subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _webSocket.SubscribeToPriceUpdatesAsync(ContractKey, A<CancellationToken>._))
            .Invokes(() => subscribed.TrySetResult());

        IAsyncEnumerator<Quote> quotes = _venue.StreamQuotesAsync(Contract, cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> next = quotes.MoveNextAsync();

        // The handler is attached before the subscribe call, so once that has run a published tick is captured.
        await subscribed.Task;
        _webSocket.PriceUpdateReceived += Raise.With(new ClientModels.PriceUpdate
        {
            Symbol = ContractKey,
            BestBid = 5_000m,
            BestAsk = 5_000.25m,
            Timestamp = new DateTime(2026, 7, 20, 14, 30, 0, DateTimeKind.Utc),
        });

        (await next).Should().BeTrue();
        quotes.Current.Bid.Should().Be(new Price(5_000m));
        quotes.Current.Ask.Should().Be(new Price(5_000.25m));
        quotes.Current.BidSize.Should().BeNull();

        await quotes.DisposeAsync();
        A.CallTo(() => _webSocket.UnsubscribeFromPriceUpdatesAsync(ContractKey, A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldIgnoreUpdates_WhenTheyAreForAnotherContract()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        TaskCompletionSource subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _webSocket.SubscribeToPriceUpdatesAsync(ContractKey, A<CancellationToken>._))
            .Invokes(() => subscribed.TrySetResult());

        IAsyncEnumerator<Quote> quotes = _venue.StreamQuotesAsync(Contract, cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> next = quotes.MoveNextAsync();
        await subscribed.Task;

        // One hub carries every subscribed contract, so the wrong instrument must not leak into this stream.
        _webSocket.PriceUpdateReceived += Raise.With(
            new ClientModels.PriceUpdate { Symbol = "CON.F.US.ENQ.M25", BestBid = 1m, BestAsk = 2m });
        _webSocket.PriceUpdateReceived += Raise.With(
            new ClientModels.PriceUpdate { Symbol = ContractKey, BestBid = 5_000m, BestAsk = 5_000.25m });

        (await next).Should().BeTrue();
        quotes.Current.Bid.Should().Be(new Price(5_000m));

        await quotes.DisposeAsync();
    }
}
