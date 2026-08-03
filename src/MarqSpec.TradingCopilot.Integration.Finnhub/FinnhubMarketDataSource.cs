using System.Threading.Channels;
using MarqSpec.Client.Finnhub;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Integration.Finnhub;

/// <summary>
/// The Finnhub adapter for the R-17 <see cref="IContextMarketDataSource"/> seam (gh#496, of gh#411): a data-only
/// <b>cross-asset context</b> price source that translates Finnhub's raw <see cref="FinnhubTrade"/> into the
/// venue-neutral <see cref="ContextTrade"/>. It declares and requires <see cref="VenueCapability.ContextTrades"/>;
/// it holds no account, executes nothing, and publishes no book.
/// </summary>
/// <remarks>
/// <para>
/// <b>It grants <see cref="VenueCapability.ContextTrades"/> and not <see cref="VenueCapability.Quotes"/></b>,
/// because Finnhub's free tier carries neither side of the top of book — only prints. Claiming <c>Quotes</c> would
/// mean publishing <c>Bid == Ask</c>, a spread nobody observed, into the stream the execution watchers act on.
/// It also does <b>not</b> grant <see cref="VenueCapability.HistoricalBars"/>: free-tier candles are a paid
/// feature, so a caller asking this source for history is refused at the seam rather than handed an empty series.
/// </para>
/// <para>
/// <b>The stream is multiplexed and single-consumer; a subscription is neither.</b> One websocket carries every
/// subscribed symbol, and <see cref="IFinnhubQuoteStream.ReadAsync"/> is a <i>single</i> reader over it — each call
/// reconnects the shared socket, so two concurrent enumerations tear down each other's transport rather than each
/// receiving a copy. The seam above promises the opposite: one independent per-contract sequence per caller. This
/// adapter is what reconciles the two, and that reconciliation is the reason it holds state at all.
/// </para>
/// <para>
/// <b>Exactly one enumeration of the transport, demultiplexed to per-subscription channels.</b> The first caller
/// starts the shared reader; every print is routed by symbol to the channels registered for it, so N callers see N
/// independent, complete sequences over one socket. When the reader ends or faults, <i>every</i> subscriber is
/// completed with that outcome — never just whichever one happened to be reading — so each
/// <c>ContextSubscriptionSupervisor</c> sees a drop and re-subscribes, and the next subscriber starts a fresh
/// reader. Before this, one supervisor per symbol meant one <c>ReadAsync</c> per symbol: subscribing the two
/// symbols this feature is built for (SPY + QQQ) had each reader disposing the other's socket in a permanent
/// reconnect war, and prints were lost for as long as both were configured (PR #607 review).
/// </para>
/// <para>
/// <b>Backpressure reaches TCP; nothing is silently shed.</b> Each subscription's buffer is small and bounded, and
/// the shared reader <i>awaits</i> a slot rather than dropping, so a consumer that stalls stops the socket being
/// drained instead of growing the heap of a process that also runs the auto-flatten watchdog. The cost is
/// deliberate and worth naming: a stalled subscriber holds up the shared reader, so a stall on one context symbol
/// delays the others. That is the right trade for an optional colour feed whose consumer is a durable append —
/// losing prints silently would be the worse failure, and unbounded buffering would put an optional feed in a
/// position to hurt the trading path. A subscriber that goes away mid-write is dropped and the rest keep flowing.
/// </para>
/// </remarks>
public sealed class FinnhubMarketDataSource : IContextMarketDataSource, IDisposable
{
    /// <summary>
    /// Per-subscription buffer depth. Large enough to absorb a burst of prints while the consumer commits the
    /// previous one; small enough that a genuinely stalled consumer reaches back to TCP quickly (see the remarks).
    /// </summary>
    private const int SubscriberBufferDepth = 256;

    private readonly IFinnhubQuoteStream _stream;
    private readonly object _gate = new();
    private readonly List<Subscriber> _subscribers = [];
    private readonly CancellationTokenSource _lifetime = new();

    private Task? _reader;
    private bool _disposed;

    /// <summary>Creates the adapter.</summary>
    /// <param name="stream">The Finnhub websocket trade feed — shared by every subscription, read exactly once.</param>
    public FinnhubMarketDataSource(IFinnhubQuoteStream stream) => _stream = stream;

    /// <inheritdoc />
    public VenueId Id { get; } = VenueId.Parse("finnhub");

    /// <inheritdoc />
    public VenueCapabilities Capabilities => VenueCapabilities.Of(VenueCapability.ContextTrades);

    /// <inheritdoc />
    public int AdapterLogicVersion => 1;

    /// <inheritdoc />
    /// <remarks>
    /// Finnhub addresses equities by their plain ticker, so the contract handle <i>is</i> the symbol — but it is
    /// still minted through <see cref="VenueContractId"/> with this source's own <see cref="Id"/>, so a Finnhub
    /// <c>SPY</c> and a ProjectX contract can never compare equal however similar their keys look.
    /// </remarks>
    public Task<ResolvedContract> ResolveContractAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken = default)
    {
        Capabilities.Require(VenueCapability.ContextTrades);

        return Task.FromResult(new ResolvedContract(VenueContractId.Create(Id, instrument.Symbol), instrument));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ContextTrade> StreamContextTradesAsync(
        VenueContractId contract,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Capabilities.Require(VenueCapability.ContextTrades);

        // Registered BEFORE the subscribe call: the shared reader may already be carrying this symbol for another
        // caller, and a print arriving between the two would otherwise fall on the floor.
        Subscriber subscriber = Register(contract.Key, cancellationToken);
        try
        {
            // Subscribe before reading, and let a refusal OUT. The free-tier cap is enforced at this call precisely
            // because Finnhub ignores an over-cap subscribe: swallowing it here would turn a loud failure back into
            // the silent never-ticks gap the client's exception exists to prevent (gh#496).
            await _stream.SubscribeAsync(contract.Key, cancellationToken);

            // One socket carries every subscribed symbol; the routing in BroadcastAsync is what makes a
            // subscription mean one instrument. Without it a SPY caller is fed QQQ's prints as SPY's.
            await foreach (ContextTrade trade in subscriber.Reader.ReadAllAsync(cancellationToken))
            {
                yield return trade;
            }
        }
        finally
        {
            Remove(subscriber);
        }
    }

    /// <summary>
    /// Stops the shared reader. The <see cref="IFinnhubQuoteStream"/> itself is <b>not</b> disposed here — it is
    /// registered separately and its scope owns it.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private Subscriber Register(string symbol, CancellationToken cancellationToken)
    {
        Subscriber subscriber = new(symbol, cancellationToken, _lifetime.Token, SubscriberBufferDepth);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _subscribers.Add(subscriber);

            // Exactly one enumeration of the transport, ever: started lazily by the first subscriber, and started
            // again by the next one only after the previous reader has finished and nulled this (see ReadAsync).
            _reader ??= Task.Run(() => ReadAsync(_lifetime.Token));
        }

        return subscriber;
    }

    private void Remove(Subscriber subscriber)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscriber);
        }

        subscriber.Dispose();
    }

    /// <summary>The single shared enumeration of the multiplexed transport.</summary>
    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        Exception? fault = null;
        try
        {
            await foreach (FinnhubTrade trade in _stream.ReadAsync(cancellationToken))
            {
                await BroadcastAsync(trade);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The adapter is being disposed: a stop, not a drop.
        }
        catch (Exception error)
        {
            fault = error;
        }
        finally
        {
            Subscriber[] orphaned;
            lock (_gate)
            {
                // Nulled under the same lock that takes the subscribers, so the next Register starts a fresh reader
                // rather than adopting one that has already finished.
                _reader = null;
                orphaned = [.. _subscribers];
                _subscribers.Clear();
            }

            foreach (Subscriber subscriber in orphaned)
            {
                // EVERY subscriber learns the feed ended, not just whichever one happened to be reading. Each
                // supervisor then treats it as a drop and re-subscribes.
                subscriber.Complete(fault);
            }
        }
    }

    private async Task BroadcastAsync(FinnhubTrade trade)
    {
        Subscriber[] targets;
        lock (_gate)
        {
            targets = [.. _subscribers.Where(subscriber => subscriber.Wants(trade.Symbol))];
        }

        if (targets.Length == 0)
        {
            return;
        }

        ContextTrade context = new(trade.TimestampUtc, new Price(trade.Price), trade.Volume);
        foreach (Subscriber target in targets)
        {
            try
            {
                await target.WriteAsync(context);
            }
            catch (Exception error)
                when (error is OperationCanceledException or ChannelClosedException or ObjectDisposedException)
            {
                // This subscriber went away between the snapshot above and the write. Drop it and keep feeding the
                // others: one departed subscriber must never stall the shared reader, which would starve every
                // other symbol on the socket.
                Remove(target);
            }
        }
    }

    /// <summary>One live subscription: its symbol, its own buffer, and its own cancellation.</summary>
    private sealed class Subscriber : IDisposable
    {
        private readonly Channel<ContextTrade> _channel;
        private readonly CancellationTokenSource _cancellation;

        public Subscriber(string symbol, CancellationToken caller, CancellationToken lifetime, int depth)
        {
            Symbol = symbol;
            _channel = Channel.CreateBounded<ContextTrade>(new BoundedChannelOptions(depth)
            {
                // Wait, never drop: the shared reader blocking is what pushes backpressure down to TCP.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });

            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime);
        }

        public string Symbol { get; }

        public ChannelReader<ContextTrade> Reader => _channel.Reader;

        public bool Wants(string symbol) => string.Equals(Symbol, symbol, StringComparison.OrdinalIgnoreCase);

        public ValueTask WriteAsync(ContextTrade trade) => _channel.Writer.WriteAsync(trade, _cancellation.Token);

        public void Complete(Exception? fault) => _channel.Writer.TryComplete(fault);

        public void Dispose()
        {
            // Completed before the token source goes, so a write racing this fails as ChannelClosedException
            // rather than on a disposed token.
            _channel.Writer.TryComplete();
            _cancellation.Dispose();
        }
    }
}
