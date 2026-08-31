using FakeItEasy;
using MarqSpec.Client.Tradovate;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Tradovate;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tradovate;

/// <summary>
/// The Tradovate venue (gh#977). It reads accounts (with host-derived mode and joined balances) and positions,
/// resolves contracts, serves historical bars, and streams live quotes; execution still refuses loudly through the
/// capability seam until its slice lands — so nothing partial or silent reaches a caller.
/// </summary>
public class TradovateVenueTests
{
    private static VenueId Tradovate { get; } = VenueId.Parse("tradovate");
    private readonly ITradovateApiClient _api = A.Fake<ITradovateApiClient>();
    private readonly ITradovateWebSocketClient _webSocket = A.Fake<ITradovateWebSocketClient>();
    private readonly TradovateQuoteSubscriptions _subscriptions = new();

    private TradovateVenue CreateVenue(string host = "https://demo.tradovateapi.com/v1", FirmConventions? conventions = null)
    {
        A.CallTo(() => _api.ConfiguredHost).Returns(host);
        return new TradovateVenue(_api, _webSocket, _subscriptions, conventions ?? FirmConventions.ForBrokerage("Tradovate"));
    }

    [Fact]
    public void Id_ShouldBeTradovate()
    {
        CreateVenue().Id.Should().Be(Tradovate);
    }

    [Fact]
    public void Capabilities_ShouldGrantBarsAndQuotes_ButNotExecution()
    {
        VenueCapabilities capabilities = CreateVenue().Capabilities;

        capabilities.Supports(VenueCapability.HistoricalBars).Should().BeTrue();
        capabilities.Supports(VenueCapability.Quotes).Should().BeTrue();
        capabilities.Supports(VenueCapability.ClosePosition).Should().BeFalse();
    }

    [Fact]
    public async Task GetAccountsAsync_ShouldJoinBalancesAndResolvePracticeOnTheDemoHost()
    {
        IReadOnlyList<ClientModels.Account> accounts = [Account(9001), Account(9002)];
        IReadOnlyList<ClientModels.CashBalance> balances = [CashBalance(9001, 7500m)]; // 9002 has no cash-balance row
        A.CallTo(() => _api.GetAccountsAsync(A<CancellationToken>._)).Returns(accounts);
        A.CallTo(() => _api.GetCashBalancesAsync(A<CancellationToken>._)).Returns(balances);

        IReadOnlyList<VenueAccount> result = await CreateVenue(host: "https://demo.tradovateapi.com/v1").GetAccountsAsync();

        result.Should().HaveCount(2);

        VenueAccount first = result.Single(account => account.Id == VenueAccountId.Create(Tradovate, "9001"));
        first.Balance.Should().Be(7500m);
        first.Mode.Should().Be(TradingMode.Practice);

        VenueAccount second = result.Single(account => account.Id == VenueAccountId.Create(Tradovate, "9002"));
        second.Balance.Should().Be(0m); // no cash-balance row -> zero, not a failed discovery
    }

    [Fact]
    public async Task GetAccountsAsync_ShouldResolveLive_OnTheLiveHost()
    {
        IReadOnlyList<ClientModels.Account> accounts = [Account(9001)];
        IReadOnlyList<ClientModels.CashBalance> balances = [];
        A.CallTo(() => _api.GetAccountsAsync(A<CancellationToken>._)).Returns(accounts);
        A.CallTo(() => _api.GetCashBalancesAsync(A<CancellationToken>._)).Returns(balances);

        IReadOnlyList<VenueAccount> result = await CreateVenue(host: "https://live.tradovateapi.com/v1").GetAccountsAsync();

        result.Single().Mode.Should().Be(TradingMode.Live);
    }

    [Fact]
    public async Task GetAccountsAsync_ShouldThrow_OnAnUnrecognisedHost_SoNoAccountIsMappedOffAnUnknownMode()
    {
        // The fail-open guard (review of #990): an unrecognised host cannot be classified practice-vs-live, so the
        // whole discovery is refused rather than mapping an account whose raw flag would later persist as Live.
        Func<Task> act = () => CreateVenue(host: "https://api.tradovate.com").GetAccountsAsync();

        await act.Should().ThrowAsync<TradovateVenueException>();
    }

    [Fact]
    public async Task GetPositionsAsync_ShouldFilterToTheAccountAndSkipFlat()
    {
        IReadOnlyList<ClientModels.Position> positions =
        [
            Position(accountId: 9001, contractId: 7, netPos: 2), // this account, open -> kept
            Position(accountId: 9001, contractId: 8, netPos: 0), // this account, flat -> skipped
            Position(accountId: 9002, contractId: 9, netPos: 5), // another account -> skipped
        ];
        A.CallTo(() => _api.GetPositionsAsync(A<CancellationToken>._)).Returns(positions);

        IReadOnlyList<PositionSnapshot> result =
            await CreateVenue().GetPositionsAsync(VenueAccountId.Create(Tradovate, "9001"));

        result.Should().ContainSingle();
        result.Single().Contract.Should().Be(VenueContractId.Create(Tradovate, "7"));
        result.Single().NetQuantity.Should().Be(2);
    }

    [Fact]
    public async Task GetPositionsAsync_ShouldThrow_ForAForeignAccount()
    {
        Func<Task> act = () =>
            CreateVenue().GetPositionsAsync(VenueAccountId.Create(VenueId.Parse("projectx"), "9001"));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ResolveContractAsync_ShouldMapAFoundContract()
    {
        A.CallTo(() => _api.FindContractAsync("ESM24", A<CancellationToken>._))
            .Returns(new ClientModels.Contract { Id = 123, Name = "ESM24", ContractMaturityId = 1 });

        ResolvedContract resolved = await CreateVenue().ResolveContractAsync(InstrumentId.Parse("ESM24"));

        resolved.Contract.Should().Be(VenueContractId.Create(Tradovate, "123"));
    }

    [Fact]
    public async Task ResolveContractAsync_ShouldThrow_WhenNoContractMatches()
    {
        A.CallTo(() => _api.FindContractAsync(A<string>._, A<CancellationToken>._)).Returns((ClientModels.Contract?)null);

        Func<Task> act = () => CreateVenue().ResolveContractAsync(InstrumentId.Parse("ZZ"));

        await act.Should().ThrowAsync<TradovateVenueException>();
    }

    [Fact]
    public async Task GetBarsAsync_ShouldMapTheBars_WhenTheMarketDataSocketIsConnected()
    {
        A.CallTo(() => _webSocket.MarketDataState).Returns(ClientModels.ConnectionState.Connected);
        IReadOnlyList<ClientModels.ChartBar> chartBars =
        [
            new ClientModels.ChartBar { Timestamp = DateTimeOffset.UnixEpoch, Open = 5000m, High = 5010m, Low = 4990m, Close = 5005m, Volume = 1234m },
        ];
        A.CallTo(() => _webSocket.GetHistoricalBarsAsync(A<ClientModels.ChartRequest>._, A<CancellationToken>._)).Returns(chartBars);

        IReadOnlyList<Bar> bars = await CreateVenue().GetBarsAsync(
            VenueContractId.Create(Tradovate, "7"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), TimeSpan.FromMinutes(5));

        bars.Should().ContainSingle();
        bars.Single().Close.Should().Be(new Price(5005m));
        bars.Single().Volume.Should().Be(1234L);
    }

    [Fact]
    public async Task GetBarsAsync_ShouldReturnBarsInAscendingTimeOrder_EvenWhenTheSocketReturnsThemUnordered()
    {
        // IMarketDataSource promises ascending order; the socket does not, so the adapter must sort (R-1 replay).
        A.CallTo(() => _webSocket.MarketDataState).Returns(ClientModels.ConnectionState.Connected);
        IReadOnlyList<ClientModels.ChartBar> outOfOrder =
        [
            ChartBar(DateTimeOffset.UnixEpoch.AddMinutes(2)),
            ChartBar(DateTimeOffset.UnixEpoch),
            ChartBar(DateTimeOffset.UnixEpoch.AddMinutes(1)),
        ];
        A.CallTo(() => _webSocket.GetHistoricalBarsAsync(A<ClientModels.ChartRequest>._, A<CancellationToken>._)).Returns(outOfOrder);

        IReadOnlyList<Bar> bars = await CreateVenue().GetBarsAsync(
            VenueContractId.Create(Tradovate, "7"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5), TimeSpan.FromMinutes(1));

        bars.Select(bar => bar.OpenTime).Should().BeInAscendingOrder();
    }

    [Theory]
    [InlineData(ClientModels.ConnectionState.Disconnected)]
    [InlineData(ClientModels.ConnectionState.Connecting)]
    [InlineData(ClientModels.ConnectionState.Reconnecting)]
    public async Task GetBarsAsync_ShouldThrowAndNeverConnect_WhenTheSocketIsNotConnected(ClientModels.ConnectionState state)
    {
        // A bars read must never drive the shared, process-wide socket's lifecycle: ConnectMarketDataAsync is NOT
        // idempotent (it reconnects without replaying subscriptions), so any non-Connected state fails loudly instead.
        A.CallTo(() => _webSocket.MarketDataState).Returns(state);

        Func<Task> act = () => CreateVenue().GetBarsAsync(
            VenueContractId.Create(Tradovate, "7"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5), TimeSpan.FromMinutes(1));

        await act.Should().ThrowAsync<TradovateVenueException>();
        A.CallTo(() => _webSocket.ConnectMarketDataAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _webSocket.GetHistoricalBarsAsync(A<ClientModels.ChartRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task GetBarsAsync_ShouldNotTouchTheSocket_ForAForeignVenueContract()
    {
        // The mapping (venue-qualifier guard) throws before any socket call — hoisting the socket work above the
        // mapping would open a connection for a request that was never valid.
        A.CallTo(() => _webSocket.MarketDataState).Returns(ClientModels.ConnectionState.Connected);

        Func<Task> act = () => CreateVenue().GetBarsAsync(
            VenueContractId.Create(VenueId.Parse("projectx"), "7"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5), TimeSpan.FromMinutes(1));

        await act.Should().ThrowAsync<ArgumentException>();
        A.CallTo(() => _webSocket.ConnectMarketDataAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _webSocket.GetHistoricalBarsAsync(A<ClientModels.ChartRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public void StreamQuotesAsync_ShouldNotSubscribe_WhenTheSequenceIsNeverEnumerated()
    {
        // Subscription lives inside the iterator, so an unconsumed stream leaves no handler or subscription behind.
        _ = CreateVenue().StreamQuotesAsync(VenueContractId.Create(Tradovate, "7"));

        A.CallTo(() => _webSocket.SubscribeQuoteAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public void StreamQuotesAsync_ShouldThrow_ForAForeignVenueContract()
    {
        // Eager qualifier guard: a projectx: contract fails at the call, before any iterator work.
        Action act = () => CreateVenue().StreamQuotesAsync(VenueContractId.Create(VenueId.Parse("projectx"), "7"));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(ClientModels.ConnectionState.Disconnected)]
    [InlineData(ClientModels.ConnectionState.Connecting)]
    [InlineData(ClientModels.ConnectionState.Reconnecting)]
    public async Task StreamQuotesAsync_ShouldThrowAndNeverConnectOrSubscribe_WhenTheSocketIsNotConnected(ClientModels.ConnectionState state)
    {
        // A quote stream must never drive the shared, process-wide socket's lifecycle: ConnectMarketDataAsync is NOT
        // idempotent (it reconnects without replaying subscriptions), so any non-Connected state fails loudly instead.
        A.CallTo(() => _webSocket.MarketDataState).Returns(state);
        Func<Task> consume = async () =>
        {
            await foreach (Quote _ in CreateVenue().StreamQuotesAsync(VenueContractId.Create(Tradovate, "7")))
            {
            }
        };

        await consume.Should().ThrowAsync<TradovateVenueException>();
        A.CallTo(() => _webSocket.ConnectMarketDataAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _webSocket.SubscribeQuoteAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldNotReemitTheBook_ForATradeOnlyFrame()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        A.CallTo(() => _webSocket.MarketDataState).Returns(ClientModels.ConnectionState.Connected);
        TaskCompletionSource subscribed = new();
        A.CallTo(() => _webSocket.SubscribeQuoteAsync("7", A<CancellationToken>._)).Invokes(() => subscribed.TrySetResult());

        IAsyncEnumerator<Quote> quotes = CreateVenue()
            .StreamQuotesAsync(VenueContractId.Create(Tradovate, "7"), cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> first = quotes.MoveNextAsync();
            await subscribed.Task;

            // Establish a complete book, consume it, then a trade-only frame (fresh timestamp, no bid/ask), then a
            // real ask move. The trade frame must NOT re-emit the unchanged book under its advancing timestamp.
            _webSocket.QuoteReceived += Raise.With(new ClientModels.Quote { ContractId = 7, Timestamp = DateTimeOffset.UnixEpoch, BidPrice = 5000m, AskPrice = 5001m });
            (await first).Should().BeTrue();
            quotes.Current.Ask.Should().Be(new Price(5001m));

            ValueTask<bool> next = quotes.MoveNextAsync();
            _webSocket.QuoteReceived += Raise.With(new ClientModels.Quote { ContractId = 7, Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(1), LastPrice = 5000.5m, LastSize = 2 });
            _webSocket.QuoteReceived += Raise.With(new ClientModels.Quote { ContractId = 7, Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(2), AskPrice = 5002m });

            // The next quote is the ask move at +2s, not a re-emit of the old book at +1s.
            (await next).Should().BeTrue();
            quotes.Current.Ask.Should().Be(new Price(5002m));
            quotes.Current.Timestamp.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(2));
        }
        finally
        {
            await quotes.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldYieldACompleteQuote_WhenBothSidesArePublished()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        A.CallTo(() => _webSocket.MarketDataState).Returns(ClientModels.ConnectionState.Connected);
        TaskCompletionSource subscribed = new();
        A.CallTo(() => _webSocket.SubscribeQuoteAsync("7", A<CancellationToken>._)).Invokes(() => subscribed.TrySetResult());

        IAsyncEnumerator<Quote> quotes = CreateVenue()
            .StreamQuotesAsync(VenueContractId.Create(Tradovate, "7"), cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> next = quotes.MoveNextAsync();
            await subscribed.Task; // the handler is attached before subscribe, so once that runs a tick is captured

            _webSocket.QuoteReceived += Raise.With(new ClientModels.Quote
            {
                ContractId = 7,
                Timestamp = DateTimeOffset.UnixEpoch,
                BidPrice = 5000m,
                BidSize = 3,
                AskPrice = 5001m,
                AskSize = 4,
            });

            (await next).Should().BeTrue();
            quotes.Current.Bid.Should().Be(new Price(5000m));
            quotes.Current.Ask.Should().Be(new Price(5001m));
            quotes.Current.BidSize.Should().Be(3L);
            quotes.Current.AskSize.Should().Be(4L);
        }
        finally
        {
            await quotes.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldAssembleACompleteQuote_FromIncrementalBidThenAskUpdates()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        A.CallTo(() => _webSocket.MarketDataState).Returns(ClientModels.ConnectionState.Connected);
        TaskCompletionSource subscribed = new();
        A.CallTo(() => _webSocket.SubscribeQuoteAsync("7", A<CancellationToken>._)).Invokes(() => subscribed.TrySetResult());

        IAsyncEnumerator<Quote> quotes = CreateVenue()
            .StreamQuotesAsync(VenueContractId.Create(Tradovate, "7"), cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> next = quotes.MoveNextAsync();
            await subscribed.Task;

            // Bid-only first: no complete snapshot yet, so nothing is emitted. Then the ask completes it.
            _webSocket.QuoteReceived += Raise.With(new ClientModels.Quote { ContractId = 7, Timestamp = DateTimeOffset.UnixEpoch, BidPrice = 5000m, BidSize = 3 });
            _webSocket.QuoteReceived += Raise.With(new ClientModels.Quote { ContractId = 7, Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(1), AskPrice = 5001m, AskSize = 4 });

            (await next).Should().BeTrue();
            quotes.Current.Bid.Should().Be(new Price(5000m));
            quotes.Current.Ask.Should().Be(new Price(5001m));
        }
        finally
        {
            await quotes.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldIgnoreQuotesForAnotherContract()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        A.CallTo(() => _webSocket.MarketDataState).Returns(ClientModels.ConnectionState.Connected);
        TaskCompletionSource subscribed = new();
        A.CallTo(() => _webSocket.SubscribeQuoteAsync("7", A<CancellationToken>._)).Invokes(() => subscribed.TrySetResult());

        IAsyncEnumerator<Quote> quotes = CreateVenue()
            .StreamQuotesAsync(VenueContractId.Create(Tradovate, "7"), cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> next = quotes.MoveNextAsync();
            await subscribed.Task;

            // Another contract's complete quote must not surface on this stream; this contract's does.
            _webSocket.QuoteReceived += Raise.With(new ClientModels.Quote { ContractId = 99, Timestamp = DateTimeOffset.UnixEpoch, BidPrice = 1m, AskPrice = 2m });
            _webSocket.QuoteReceived += Raise.With(new ClientModels.Quote { ContractId = 7, Timestamp = DateTimeOffset.UnixEpoch, BidPrice = 5000m, AskPrice = 5001m });

            (await next).Should().BeTrue();
            quotes.Current.Bid.Should().Be(new Price(5000m));
        }
        finally
        {
            await quotes.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldUnsubscribe_WhenTheStreamIsDisposed()
    {
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        A.CallTo(() => _webSocket.MarketDataState).Returns(ClientModels.ConnectionState.Connected);
        TaskCompletionSource subscribed = new();
        A.CallTo(() => _webSocket.SubscribeQuoteAsync("7", A<CancellationToken>._)).Invokes(() => subscribed.TrySetResult());

        IAsyncEnumerator<Quote> quotes = CreateVenue()
            .StreamQuotesAsync(VenueContractId.Create(Tradovate, "7"), cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> next = quotes.MoveNextAsync();
        await subscribed.Task;
        _webSocket.QuoteReceived += Raise.With(new ClientModels.Quote { ContractId = 7, Timestamp = DateTimeOffset.UnixEpoch, BidPrice = 5000m, AskPrice = 5001m });
        (await next).Should().BeTrue(); // a quote arrived; the iterator is now parked at its yield

        await quotes.DisposeAsync(); // disposing a consumer that walks away runs the iterator's finally

        A.CallTo(() => _webSocket.UnsubscribeQuoteAsync("7", A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldRegisterTheSubscription_SoAHostDrivenReconnectReplaysIt()
    {
        // The connection host reconnects the shared socket through the client's NON-replaying path, so a live
        // stream is only re-armed if it is on the register. An unregistered stream survives the drop as an open
        // sequence that never ticks again — the silent failure that stalls stop promotion.
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        A.CallTo(() => _webSocket.MarketDataState).Returns(ClientModels.ConnectionState.Connected);
        TaskCompletionSource subscribed = new();
        A.CallTo(() => _webSocket.SubscribeQuoteAsync("7", A<CancellationToken>._)).Invokes(() => subscribed.TrySetResult());

        IAsyncEnumerator<Quote> quotes = CreateVenue()
            .StreamQuotesAsync(VenueContractId.Create(Tradovate, "7"), cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> next = quotes.MoveNextAsync();
        await subscribed.Task;
        _webSocket.QuoteReceived += Raise.With(Book(5000m, 5001m));
        (await next).Should().BeTrue(); // park the iterator at its yield, so disposal runs its finally

        _subscriptions.LiveKeys.Should().ContainSingle().Which.Should().Be("7");

        await quotes.DisposeAsync();

        _subscriptions.LiveKeys.Should().BeEmpty("a torn-down stream must not be replayed onto a recovered socket");
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldKeepTheWireSubscribed_UntilTheLastStreamOnTheContractIsDisposed()
    {
        // One socket, one subscription per contract: unsubscribing on the FIRST teardown would starve the second
        // consumer, which still believes it is streaming.
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        A.CallTo(() => _webSocket.MarketDataState).Returns(ClientModels.ConnectionState.Connected);

        int subscribes = 0;
        TaskCompletionSource first = new();
        TaskCompletionSource second = new();
        A.CallTo(() => _webSocket.SubscribeQuoteAsync("7", A<CancellationToken>._)).Invokes(() =>
        {
            if (Interlocked.Increment(ref subscribes) == 1)
            {
                first.TrySetResult();
            }
            else
            {
                second.TrySetResult();
            }
        });

        TradovateVenue venue = CreateVenue();
        IAsyncEnumerator<Quote> one = venue
            .StreamQuotesAsync(VenueContractId.Create(Tradovate, "7"), cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> firstNext = one.MoveNextAsync();
        await first.Task;

        IAsyncEnumerator<Quote> two = venue
            .StreamQuotesAsync(VenueContractId.Create(Tradovate, "7"), cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> secondNext = two.MoveNextAsync();
        await second.Task;

        // One book feeds both streams -- each attached its own handler to the shared socket.
        _webSocket.QuoteReceived += Raise.With(Book(5000m, 5001m));
        (await firstNext).Should().BeTrue();
        (await secondNext).Should().BeTrue();

        await one.DisposeAsync();

        A.CallTo(() => _webSocket.UnsubscribeQuoteAsync("7", A<CancellationToken>._)).MustNotHaveHappened();
        _subscriptions.LiveKeys.Should().ContainSingle().Which.Should().Be("7");

        await two.DisposeAsync();

        A.CallTo(() => _webSocket.UnsubscribeQuoteAsync("7", A<CancellationToken>._)).MustHaveHappened();
        _subscriptions.LiveKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamQuotesAsync_ShouldStillReleaseTheRegistration_WhenTheWireUnsubscribeFails()
    {
        // Teardown usually happens BECAUSE the socket dropped, and the client refuses to send on a dead transport.
        // That must not surface to the consumer as a fault, and must not leave the key on the register — replaying
        // a subscription nobody consumes would feed a channel with no reader for the rest of the session.
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        A.CallTo(() => _webSocket.MarketDataState).Returns(ClientModels.ConnectionState.Connected);
        TaskCompletionSource subscribed = new();
        A.CallTo(() => _webSocket.SubscribeQuoteAsync("7", A<CancellationToken>._)).Invokes(() => subscribed.TrySetResult());
        A.CallTo(() => _webSocket.UnsubscribeQuoteAsync("7", A<CancellationToken>._))
            .Throws(new InvalidOperationException("The WebSocket is not connected."));

        IAsyncEnumerator<Quote> quotes = CreateVenue()
            .StreamQuotesAsync(VenueContractId.Create(Tradovate, "7"), cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> next = quotes.MoveNextAsync();
        await subscribed.Task;
        _webSocket.QuoteReceived += Raise.With(Book(5000m, 5001m));
        (await next).Should().BeTrue();

        Func<Task> dispose = async () => await quotes.DisposeAsync();

        await dispose.Should().NotThrowAsync();
        _subscriptions.LiveKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ClosePositionAsync_ShouldRefuse_WhileClosePositionIsUngranted()
    {
        Func<Task> act = () => CreateVenue().ClosePositionAsync(
            VenueAccountId.Create(Tradovate, "9001"), VenueContractId.Create(Tradovate, "7"));

        await act.Should().ThrowAsync<VenueCapabilityNotSupportedException>();
    }

    [Fact]
    public async Task PlaceOrderAsync_ShouldRefuse_InTheReadOnlySlice()
    {
        Func<Task> act = () => CreateVenue().PlaceOrderAsync(null!);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task CancelOrderAsync_ShouldRefuse_InTheReadOnlySlice()
    {
        Func<Task> act = () => CreateVenue().CancelOrderAsync(VenueAccountId.Create(Tradovate, "9001"), "1");

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static ClientModels.Quote Book(decimal bid, decimal ask) =>
        new() { ContractId = 7, Timestamp = DateTimeOffset.UnixEpoch, BidPrice = bid, AskPrice = ask };

    private static ClientModels.Account Account(long? id, bool active = true, bool isReadonly = false) =>
        new()
        {
            Id = id,
            Name = "Acct",
            UserId = 1,
            AccountType = default,
            Active = active,
            ClearingHouseId = 1,
            RiskCategoryId = 1,
            AutoLiqProfileId = 1,
            MarginAccountType = default,
            LegalStatus = default,
            Readonly = isReadonly,
        };

    private static ClientModels.CashBalance CashBalance(long accountId, decimal amount) =>
        new()
        {
            AccountId = accountId,
            Timestamp = DateTimeOffset.UnixEpoch,
            TradeDate = new ClientModels.TradeDate { Year = 2026, Month = 8, Day = 18 },
            CurrencyId = 1,
            Amount = amount,
        };

    private static ClientModels.Position Position(long accountId, long contractId, int netPos) =>
        new()
        {
            AccountId = accountId,
            ContractId = contractId,
            Timestamp = DateTimeOffset.UnixEpoch,
            TradeDate = new ClientModels.TradeDate { Year = 2026, Month = 8, Day = 18 },
            NetPos = netPos,
            NetPrice = 5000m,
            Bought = 0,
            BoughtValue = 0m,
            Sold = 0,
            SoldValue = 0m,
            PrevPos = 0,
        };

    private static ClientModels.ChartBar ChartBar(DateTimeOffset timestamp) =>
        new() { Timestamp = timestamp, Open = 1m, High = 1m, Low = 1m, Close = 1m, Volume = 1m };
}
