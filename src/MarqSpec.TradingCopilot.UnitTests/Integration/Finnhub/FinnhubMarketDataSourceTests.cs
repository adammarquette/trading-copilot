using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FakeItEasy;
using MarqSpec.Client.Finnhub;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Finnhub;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Finnhub;

/// <summary>
/// The Finnhub → <see cref="IContextMarketDataSource"/> adapter (gh#496, of gh#411). It translates Finnhub's raw
/// <see cref="FinnhubTrade"/> into the venue-neutral <see cref="ContextTrade"/> the context ingestion path
/// consumes, and it <b>demultiplexes one single-consumer socket into one sequence per subscription</b> — the
/// mapping, the capability boundary and that fan-out are the whole job, so that is what these guard, against a
/// faked stream (no network).
/// </summary>
public sealed class FinnhubMarketDataSourceTests : IDisposable
{
    private const string Symbol = "SPY";

    private readonly IFinnhubQuoteStream _stream = A.Fake<IFinnhubQuoteStream>();
    private FinnhubMarketDataSource? _source;

    private FinnhubMarketDataSource Source() => _source ??= new FinnhubMarketDataSource(_stream);

    public void Dispose() => _source?.Dispose();

    private static VenueContractId Contract(string symbol = Symbol) =>
        VenueContractId.Create(VenueId.Parse("finnhub"), symbol);

    private static FinnhubTrade Trade(
        string symbol = Symbol,
        decimal price = 512.34m,
        decimal volume = 250m,
        long epochMs = 1_700_000_000_000) =>
        new(symbol, price, volume, DateTimeOffset.FromUnixTimeMilliseconds(epochMs));

    private void StreamYields(params FinnhubTrade[] trades) =>
        A.CallTo(() => _stream.ReadAsync(A<CancellationToken>._)).Returns(ToAsync(trades));

    private static async IAsyncEnumerable<FinnhubTrade> ToAsync(IEnumerable<FinnhubTrade> trades)
    {
        foreach (FinnhubTrade trade in trades)
        {
            yield return trade;
        }

        await Task.CompletedTask;
    }

    // A hung subscription is a test that never returns, so every drain carries its own deadline: a broken fan-out
    // should fail this suite, not wedge CI.
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(10));

    private async Task<List<ContextTrade>> DrainAsync(VenueContractId? contract = null)
    {
        using CancellationTokenSource deadline = Deadline();

        return await DrainAsync(Source(), contract ?? Contract(), deadline.Token);
    }

    private static async Task<List<ContextTrade>> DrainAsync(
        IContextMarketDataSource source,
        VenueContractId contract,
        CancellationToken cancellationToken)
    {
        List<ContextTrade> drained = [];
        await foreach (ContextTrade trade in source.StreamContextTradesAsync(contract, cancellationToken))
        {
            drained.Add(trade);
        }

        return drained;
    }

    [Fact]
    public void Id_ShouldBeFinnhub_SoAContextPrintTagsTheRightSource()
    {
        // Provenance is recorded from source.Id, and it is what keeps a Finnhub SPY print distinguishable from a
        // ProjectX ES quote once both are in the log.
        Source().Id.ToString().Should().Be("finnhub");
    }

    [Fact]
    public void Capabilities_ShouldGrantContextTrades_ButNeverQuotes()
    {
        // THE capability boundary this adapter exists to hold (gh#496, operator decision 2026-08-02). Finnhub's
        // free tier publishes no book, so granting Quotes would advertise executable top-of-book prices this
        // source cannot produce without inventing a zero spread — in the one stream the execution watchers act on.
        VenueCapabilities capabilities = Source().Capabilities;

        capabilities.Supports(VenueCapability.ContextTrades).Should().BeTrue();
        capabilities.Supports(VenueCapability.Quotes).Should().BeFalse(
            "a context source has no book; claiming Quotes would mean publishing a spread nobody observed");
    }

    [Fact]
    public void Capabilities_ShouldNotGrantHistoricalBars_BecauseFreeTierCandlesArePaid()
    {
        // Refusing at the seam beats returning an empty series: a caller that asked for history and got nothing
        // cannot tell "no bars exist" from "this source will never have any" (R-17).
        Source().Capabilities.Supports(VenueCapability.HistoricalBars).Should().BeFalse();
    }

    [Fact]
    public void Capabilities_ShouldGrantNothingExecutionShaped_SoADataOnlySourceCanNeverBeTraded()
    {
        VenueCapabilities capabilities = Source().Capabilities;

        capabilities.Supports(VenueCapability.BracketOrders).Should().BeFalse();
        capabilities.Supports(VenueCapability.ClosePosition).Should().BeFalse();
        capabilities.Supports(VenueCapability.AccountStreaming).Should().BeFalse();
        capabilities.Supports(VenueCapability.ModifyOrder).Should().BeFalse();
    }

    [Fact]
    public void Capabilities_ShouldGrantExactlyContextTrades_SoTheGrantCannotSilentlyWiden()
    {
        // The exhaustive backstop under the three boundaries above: whatever a future edit adds — News, a second
        // data capability, an execution flag — trips exact equality, so the one capability this gh#496 split exists
        // to isolate cannot broaden without a test going red, not only the specific flags spelled out above.
        Source().Capabilities.Flags.Should().Be(VenueCapability.ContextTrades);
    }

    [Theory]
    [InlineData(VenueCapability.Quotes)]         // the drift that would put a context feed on the execution path
    [InlineData(VenueCapability.HistoricalBars)] // a paid feature the seam refuses rather than serving empty
    public void CapabilitiesRequire_ShouldThrowNamingTheCapability_WhenTheRealAdapterDoesNotGrantIt(
        VenueCapability ungranted)
    {
        // gh#705 case 5 asked for the absent-grant refusal "confirmed against the real Finnhub adapter", but as
        // written that is unreachable: the adapter is sealed and always grants ContextTrades, so no test can put it
        // in the state its operations' Require(ContextTrades) guards. What IS witnessable on the real adapter is the
        // composition those operations rely on — its own Capabilities.Require refusing a capability it does not
        // grant. The tests above assert Supports (the predicate); this asserts Require (the throwing guard
        // ResolveContractAsync / StreamContextTradesAsync actually call). The Require call-site inside those two
        // methods stays behaviourally unobservable by construction and is guarded at the integration tier (#711) —
        // the accepted residual of re-scoping rather than adding a test-only production seam (gh#712, option 2).
        VenueCapabilities capabilities = Source().Capabilities;

        Action require = () => capabilities.Require(ungranted);

        require.Should().Throw<VenueCapabilityNotSupportedException>()
            .Which.MissingCapability.Should().Be(ungranted);
    }

    [Fact]
    public async Task ResolveContract_ShouldMapTheInstrumentToAFinnhubContract_PairedWithWhatItResolvedFor()
    {
        ResolvedContract resolved = await Source().ResolveContractAsync(InstrumentId.Parse(Symbol), CancellationToken.None);

        resolved.Contract.Should().Be(Contract());
        resolved.Instrument.Symbol.Should().Be(Symbol,
            "the pairing is what lets a caller detect a context symbol being used as a tradeable contract");
    }

    [Fact]
    public async Task StreamContextTrades_ShouldSubscribeTheSymbol_BeforeAnythingCanArrive()
    {
        StreamYields(Trade());

        await DrainAsync();

        A.CallTo(() => _stream.SubscribeAsync(Symbol, A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task StreamContextTrades_ShouldMapPriceSizeAndTimestamp_FromThePrint()
    {
        StreamYields(Trade(price: 512.34m, volume: 250m, epochMs: 1_700_000_000_000));

        List<ContextTrade> drained = await DrainAsync();

        drained.Should().ContainSingle();
        drained[0].Price.Value.Should().Be(512.34m);
        drained[0].Size.Should().Be(250m);
        drained[0].Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
    }

    [Fact]
    public async Task StreamContextTrades_ShouldYieldOnlyTheRequestedSymbol_WhenTheSocketIsMultiplexed()
    {
        // One websocket carries every subscribed symbol, so without per-symbol routing a caller asking for SPY
        // would be fed QQQ's prints as SPY's — two instruments conflated at the seam, which is the exact failure
        // the venue/source tagging exists to prevent. The control print is what makes this non-vacuous.
        StreamYields(
            Trade(symbol: "QQQ", price: 444.00m),
            Trade(symbol: Symbol, price: 512.34m),
            Trade(symbol: "QQQ", price: 445.00m));

        List<ContextTrade> drained = await DrainAsync(Contract(Symbol));

        drained.Should().ContainSingle("only the requested symbol's prints belong to this subscription");
        drained[0].Price.Value.Should().Be(512.34m);
    }

    [Fact]
    public async Task StreamContextTrades_ShouldSurfaceTheFreeTierCap_RatherThanStreamingSilentlyNothing()
    {
        // Finnhub ignores an over-cap subscribe, so the symbol would simply never tick and the gap would be found
        // later by its absence. The client turns that into a refusal; the adapter must let it out (an acceptance
        // criterion of gh#496: exceeding the cap fails loudly at the seam).
        A.CallTo(() => _stream.SubscribeAsync(A<string>._, A<CancellationToken>._))
            .Throws(new FinnhubSubscriptionLimitException(50, Symbol));

        Func<Task> drain = () => DrainAsync();

        await drain.Should().ThrowAsync<FinnhubSubscriptionLimitException>();
    }

    // ---- the multi-symbol fan-out: one single-consumer socket, N independent subscriptions (PR #607 review) ----

    [Fact]
    public async Task StreamContextTrades_ShouldGiveEachSymbolItsOwnCompleteSequence_WhenTwoSubscriptionsShareTheSocket()
    {
        // THE case this feature is built for and the one nothing exercised: `.env.example` and `docker-compose.yml`
        // both configure two context slots, and the PR's worked example is SPY + QQQ. The host runs one supervisor
        // per symbol, so two subscriptions are live against ONE socket at once.
        //
        // `IFinnhubQuoteStream.ReadAsync` is single-consumer — every call reconnects the shared socket and DISPOSES
        // the previous reader's transport — so a subscription-per-ReadAsync design has the two symbols destroying
        // each other's transport in a permanent reconnect war, losing prints for as long as both are configured.
        // `SingleConsumerTransport` models that contract as a hard failure, so this cannot pass by accident.
        using SingleConsumerTransport transport = new();
        using FinnhubMarketDataSource source = new(transport);
        using CancellationTokenSource deadline = Deadline();

        Task<List<ContextTrade>> spy = Task.Run(
            () => DrainAsync(source, Contract("SPY"), deadline.Token), deadline.Token);
        Task<List<ContextTrade>> qqq = Task.Run(
            () => DrainAsync(source, Contract("QQQ"), deadline.Token), deadline.Token);

        // Both must be registered before anything is pushed: a print predating a subscription is legitimately not
        // that subscription's, so pushing early would test nothing.
        await transport.NextSubscribeAsync(deadline.Token);
        await transport.NextSubscribeAsync(deadline.Token);

        transport.Push(Trade(symbol: "SPY", price: 512.34m));
        transport.Push(Trade(symbol: "QQQ", price: 444.00m));
        transport.Push(Trade(symbol: "SPY", price: 512.55m));
        transport.Push(Trade(symbol: "QQQ", price: 444.25m));
        transport.EndFeed();

        List<ContextTrade> spyPrints = await spy;
        List<ContextTrade> qqqPrints = await qqq;

        // EVERY print, not a non-deterministic subset — the whole point. Both sides asserted, so a fan-out that
        // silently favours one subscriber fails here.
        spyPrints.Select(print => print.Price.Value).Should().Equal([512.34m, 512.55m],
            "each subscription must receive every print for its own symbol, in order");
        qqqPrints.Select(print => print.Price.Value).Should().Equal([444.00m, 444.25m],
            "the second subscription must not be starved by the first sharing the socket");

        transport.MaxConcurrentReaders.Should().Be(1,
            "the transport is single-consumer; two concurrent ReadAsync enumerations tear down each other's socket");
    }

    [Fact]
    public async Task StreamContextTrades_ShouldReadTheTransportExactlyOnce_HoweverManySymbolsSubscribe()
    {
        // The structural half of the case above: not merely "both got their prints" but "there was only ever one
        // reader". Asserting the observable outcome alone would still pass a design that opened a second socket
        // and happened to interleave luckily on a fast fake.
        using SingleConsumerTransport transport = new();
        using FinnhubMarketDataSource source = new(transport);
        using CancellationTokenSource deadline = Deadline();

        Task<List<ContextTrade>> spy = Task.Run(
            () => DrainAsync(source, Contract("SPY"), deadline.Token), deadline.Token);
        Task<List<ContextTrade>> qqq = Task.Run(
            () => DrainAsync(source, Contract("QQQ"), deadline.Token), deadline.Token);

        await transport.NextSubscribeAsync(deadline.Token);
        await transport.NextSubscribeAsync(deadline.Token);
        transport.EndFeed();

        await spy;
        await qqq;

        transport.TotalReads.Should().Be(1, "two subscriptions must share one enumeration of the multiplexed socket");
        transport.Subscribed.Should().BeEquivalentTo(["SPY", "QQQ"],
            "each subscription still subscribes its own symbol on the shared socket");
    }

    [Fact]
    public async Task StreamContextTrades_ShouldEndEverySubscription_WhenTheSharedReaderFaults()
    {
        // With one shared reader, a fault reaches whichever task is enumerating it. If that outcome were not fanned
        // out, the OTHER subscriptions would hang on a channel nobody will ever write to again — their supervisors
        // would never see a drop, never back off, and never re-subscribe, and the feed would be silently dead while
        // the process looked healthy.
        using SingleConsumerTransport transport = new();
        using FinnhubMarketDataSource source = new(transport);
        using CancellationTokenSource deadline = Deadline();

        Task<List<ContextTrade>> spy = Task.Run(
            () => DrainAsync(source, Contract("SPY"), deadline.Token), deadline.Token);
        Task<List<ContextTrade>> qqq = Task.Run(
            () => DrainAsync(source, Contract("QQQ"), deadline.Token), deadline.Token);

        await transport.NextSubscribeAsync(deadline.Token);
        await transport.NextSubscribeAsync(deadline.Token);

        transport.FaultFeed(new IOException("the websocket dropped"));

        Func<Task> spyDrain = () => spy;
        Func<Task> qqqDrain = () => qqq;

        await spyDrain.Should().ThrowAsync<IOException>("the subscription reading the transport sees the fault");
        await qqqDrain.Should().ThrowAsync<IOException>(
            "and so must every other subscription, or its supervisor never re-subscribes");
    }

    /// <summary>
    /// A transport that behaves like the real <see cref="FinnhubQuoteStream"/>: <b>one reader</b>. Every real
    /// <c>ReadAsync</c> call reconnects the shared socket and disposes the previous reader's transport, so two
    /// concurrent enumerations are a defect rather than a fan-out — modelled here as a hard failure so no test can
    /// pass against a design the live client would break. Sequential re-reads (the client's own reconnect) are
    /// allowed, which is why this counts <i>concurrent</i> readers rather than total ones.
    /// </summary>
    private sealed class SingleConsumerTransport : IFinnhubQuoteStream
    {
        private readonly Channel<FinnhubTrade> _trades = Channel.CreateUnbounded<FinnhubTrade>();
        private readonly Channel<string> _subscribes = Channel.CreateUnbounded<string>();
        private readonly HashSet<string> _subscribed = new(StringComparer.OrdinalIgnoreCase);

        private int _concurrentReaders;
        private int _maxConcurrentReaders;
        private int _totalReads;

        public IReadOnlyCollection<string> Subscribed
        {
            get
            {
                lock (_subscribed)
                {
                    return [.. _subscribed];
                }
            }
        }

        public int MaxConcurrentReaders => Volatile.Read(ref _maxConcurrentReaders);

        public int TotalReads => Volatile.Read(ref _totalReads);

        public Task SubscribeAsync(string symbol, CancellationToken cancellationToken)
        {
            lock (_subscribed)
            {
                _subscribed.Add(symbol);
            }

            _subscribes.Writer.TryWrite(symbol);

            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken)
        {
            lock (_subscribed)
            {
                _subscribed.Remove(symbol);
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<FinnhubTrade> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _totalReads);

            int concurrent = Interlocked.Increment(ref _concurrentReaders);
            InterlockedMax(ref _maxConcurrentReaders, concurrent);
            try
            {
                if (concurrent > 1)
                {
                    throw new InvalidOperationException(
                        "IFinnhubQuoteStream.ReadAsync is single-consumer: a second concurrent enumeration "
                        + "reconnects the shared socket and disposes the first reader's transport. The adapter must "
                        + "read once and demultiplex by symbol (PR #607 review).");
                }

                await foreach (FinnhubTrade trade in _trades.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return trade;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentReaders);
            }
        }

        /// <summary>Waits for the next <c>SubscribeAsync</c> call, so a test can push only once a caller is live.</summary>
        public async Task<string> NextSubscribeAsync(CancellationToken cancellationToken) =>
            await _subscribes.Reader.ReadAsync(cancellationToken);

        public void Push(FinnhubTrade trade) => _trades.Writer.TryWrite(trade);

        public void EndFeed() => _trades.Writer.TryComplete();

        public void FaultFeed(Exception error) => _trades.Writer.TryComplete(error);

        public void Dispose() => _trades.Writer.TryComplete();

        private static void InterlockedMax(ref int target, int value)
        {
            int seen = Volatile.Read(ref target);
            while (value > seen)
            {
                int previous = Interlocked.CompareExchange(ref target, value, seen);
                if (previous == seen)
                {
                    return;
                }

                seen = previous;
            }
        }
    }
}
