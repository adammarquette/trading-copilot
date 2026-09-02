using System.Globalization;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.Integration.Tradovate;
using Microsoft.Extensions.Logging;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tradovate;

/// <summary>
/// The Tradovate account-event seam (R-17, gh#977). Two things carry this suite, and neither is a happy path.
/// <b>Attribution</b>: a Tradovate fill entity names only an order, never an account, so the stream holds the
/// order → account map and must never guess when it misses. <b>Silence</b>: a trading socket that drops leaves an
/// open sequence that never ticks again, which looks exactly like a quiet account to everything above — so a drop
/// has to end the stream loudly and let the supervisor re-subscribe.
/// </summary>
/// <remarks>
/// The socket double is hand-rolled rather than a fake, for one reason: it counts event subscribers, which is what
/// makes the teardown guard able to fail. A leaked handler is otherwise invisible — it writes into a channel nobody
/// reads, throws nothing, and changes no observable output — so a "handlers are detached" test written against a
/// fake would pass whether or not the code detaches anything.
/// </remarks>
public class TradovateAccountEventStreamTests
{
    private static VenueId Tradovate { get; } = VenueId.Parse("tradovate");

    private static VenueId ProjectX { get; } = VenueId.Parse("projectx");

    private static DateTimeOffset At { get; } = new(2026, 3, 4, 14, 30, 0, TimeSpan.Zero);

    /// <summary>Every await in this suite is bounded: an unbounded one reads as slow CI rather than a red test.</summary>
    private static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Mirrors <c>TradovateAccountEventStream.MaxHeldFills</c> — the hold buffer's cap.</summary>
    private const int HoldCap = 512;

    private readonly RecordingWebSocketClient _webSocket = new();

    private readonly CapturingLogger _log = new();

    // A socket the connection host has already synced. Most tests below are about attribution or silence and need a
    // socket that is genuinely delivering, so that is the default; the tests that care about the unsynced window
    // drive the real transition themselves with OnSocketConnected, because setting TradingState alone would arrange
    // an ordering production cannot produce (gh#1051 review).
    private readonly TradovateTradingSocketSync _sync = Synced();

    private static TradovateTradingSocketSync Synced()
    {
        TradovateTradingSocketSync sync = new();
        sync.CompleteObservedSync();
        return sync;
    }

    private TradovateAccountEventStream CreateStream() => new(_webSocket, _sync, _log);

    [Fact]
    public void Venue_ShouldBeTradovate()
    {
        CreateStream().Venue.Should().Be(Tradovate);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The socket's lifecycle is not this seam's to drive
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task StreamAsync_ShouldThrow_WhenTheTradingSocketIsNotConnected()
    {
        // The connection host owns the socket (gh#977 / gh#1048). A stream that connected it here would land on the
        // manual path, which sends no user/syncrequest -- connected, authorized and permanently silent.
        _webSocket.TradingState = ClientModels.ConnectionState.Disconnected;
        TradovateAccountEventStream stream = CreateStream();

        Func<Task> read = async () =>
        {
            await using Reader reader = Start(stream, [Account(9001)]);
            await reader.NextAsync();
        };

        await read.Should().ThrowAsync<TradovateVenueException>();
    }

    [Fact]
    public async Task StreamAsync_ShouldStillReceiveTheSyncSnapshot_WhenItOpensOverAConnectedButUnsyncedSocket()
    {
        // THE regression this seam must never take, and the reason it does not refuse an unsynced socket (gh#1051
        // review, finding 1). IsSynced can only become true AFTER SyncCompleted has already been raised -- the
        // client raises it synchronously from inside SyncRequestAsync before that call returns, and every path that
        // clears the obligation runs from inside that same invocation list. So a stream gated on IsSynced could only
        // ever attach on the far side of the snapshot, which makes the OnSync handler UNREACHABLE.
        //
        // OnSync is the sole source of the order -> account seed for orders predating the connect, and of the fills
        // that executed while the socket was down, which exist nowhere else. Losing it means no Fill row, no composed
        // Trade, and a realized loss that never reaches the R-5 governor -- the exact harm gh#1051 cites as its own
        // motivation. Refusing here would reintroduce it through the card's own fix.
        _sync.OnSocketConnected();
        _sync.IsSynced.Should().BeFalse("the arrangement is only meaningful over a socket nothing has synced yet");

        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        // The connection host's sync lands while this stream is open, exactly as it does in production.
        _webSocket.RaiseSync(
            orders: [Order(id: 5150, account: 9001, ClientModels.OrderStatus.Filled)],
            fills: [Fill(id: 77, order: 5150)]);

        FillEvent fill = (await reader.NextAsync()).Should().BeOfType<FillEvent>().Subject;
        fill.VenueFillKey.Should().Be("77");
        fill.Account.Should().Be(Account(9001));
    }

    [Fact]
    public async Task StreamAsync_ShouldEndTheStream_WhenTheConnectionIsReplacedBetweenTheCheckAndTheAttach()
    {
        // The window the connected re-read alone cannot cover (gh#1051). A drop AND a reconnect both landing between
        // the check and the attach leave TradingState back at Connected -- so the state re-read is satisfied -- while
        // the socket underneath is a different connection whose drop was raised before this stream's handler
        // existed. Nothing completes the channel and the read parks forever over a connection it never subscribed
        // to. Only the generation shows it.
        //
        // The message is asserted so this cannot be satisfied by the DROP path: the socket never left the connected
        // state as far as this stream could see, and reporting it as a drop would send the supervisor after the
        // wrong cause.
        _webSocket.WhenOrderHandlerAttached = () => _sync.OnSocketConnected();
        _webSocket.TradingState.Should().Be(ClientModels.ConnectionState.Connected);
        TradovateAccountEventStream stream = CreateStream();

        Func<Task> read = async () =>
        {
            await using Reader reader = Start(stream, [Account(9001)]);
            await reader.NextAsync();
        };

        await read.Should().ThrowAsync<TradovateVenueException>().WithMessage("*replaced by a new connection*");
    }

    [Fact]
    public async Task StreamAsync_ShouldNotEndTheStream_WhenTheSocketIsMerelyUnsyncedAtTheAttach()
    {
        // The other side of the guard above, and what keeps it from becoming the refusal this card must not ship. A
        // connection that is legitimately new -- and therefore legitimately unsynced -- is the ORDINARY case the
        // stream has to survive, because it is the only moment at which the snapshot can still be caught. Only a
        // generation that moved WHILE the stream was opening ends it.
        _sync.OnSocketConnected();

        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseOrder(Order(id: 5150, account: 9001, ClientModels.OrderStatus.Working));

        (await reader.NextAsync()).Should().BeOfType<OrderStateEvent>();
    }

    [Fact]
    public async Task StreamAsync_ShouldReportOnTeardown_WhenItNeverRodeASyncedSocketAtAll()
    {
        // The fact gh#1051 exists to make readable, at the one moment it is unambiguous. This stream opened over a
        // socket that had never been synced and no snapshot landed while it was open, so Tradovate pushed it no
        // props frame at all -- its silence is NOT evidence about the account, and a caller that read it as a quiet
        // one would be reading a position the platform cannot see.
        //
        // Reported rather than thrown: refusing at the top would make the snapshot unreachable (see above), and the
        // socket that stays up and never syncs is escalated to the OPERATOR by the connection host, which is where
        // something can act on it.
        _sync.OnSocketConnected();

        await using (Reader reader = Start(CreateStream(), [Account(9001)]))
        {
            await Task.Yield();
        }

        _log.Warnings.Should().Contain(message => message.Contains("delivered nothing at all"));
    }

    [Fact]
    public async Task StreamAsync_ShouldNotReportANeverSyncedSocket_WhenTheSnapshotArrivedWhileItWasOpen()
    {
        // The mirror, so the report above cannot be a constant. A stream that opened unsynced and then saw the
        // snapshot land rode a socket that was genuinely delivering, and saying otherwise would train the reader to
        // ignore the message that matters.
        _sync.OnSocketConnected();

        await using (Reader reader = Start(CreateStream(), [Account(9001)]))
        {
            _webSocket.RaiseSync(
                orders: [Order(id: 5150, account: 9001, ClientModels.OrderStatus.Filled)],
                fills: [Fill(id: 77, order: 5150)]);
            (await reader.NextAsync()).Should().BeOfType<FillEvent>();
        }

        _log.Warnings.Should().NotContain(message => message.Contains("delivered nothing at all"));
    }


    [Fact]
    public async Task StreamAsync_ShouldNotReportSilence_WhenItDeliveredLiveFramesWithoutASnapshot()
    {
        // Round-2 review, note b. The report was driven by the SNAPSHOT alone, so a stream that took live props
        // frames and never saw a further SyncCompleted was told its silence proved nothing -- while it was busy
        // emitting real orders and fills. That happens in production: the client's own reconnect syncs while one of
        // the host's syncs is in flight, so the completion is left to the connection-bound clear and the obligation
        // stays armed even though the socket really is synced.
        //
        // A message whose whole job is to say "this silence is not evidence" is worthless the moment it fires on a
        // stream that was not silent, because that is what teaches a reader to skip it.
        _sync.OnSocketConnected();

        await using (Reader reader = Start(CreateStream(), [Account(9001)]))
        {
            _webSocket.RaiseOrder(Order(id: 5150, account: 9001, ClientModels.OrderStatus.Working));
            (await reader.NextAsync()).Should().BeOfType<OrderStateEvent>();
        }

        _log.Warnings.Should().NotContain(message => message.Contains("delivered nothing at all"));
    }

    [Fact]
    public async Task StreamAsync_ShouldNotReportSilence_WhenTheSocketWasSyncedWhileTheStreamWasAttaching()
    {
        // The other half of note b. `openedUnsynced` is sampled before the handlers go on, so a SyncCompleted
        // landing in that gap clears the register while this stream's own OnSync does not yet exist -- and the
        // stream then rides a fully synced socket for its whole life. Over a genuinely quiet account it delivers
        // nothing, which is exactly when the report must NOT claim that its silence proves nothing: the socket was
        // synced, so the silence is real evidence.
        //
        // The register is the third conjunct that distinguishes the two, and this is the test that makes it
        // load-bearing rather than defensive.
        _sync.OnSocketConnected();
        _webSocket.WhenOrderHandlerAttached = () => _sync.CompleteObservedSync();

        await using (Reader reader = Start(CreateStream(), [Account(9001)]))
        {
            await Task.Yield();
        }

        _sync.IsSynced.Should().BeTrue("the arrangement is only meaningful if the socket really did sync");
        _log.Warnings.Should().NotContain(message => message.Contains("delivered nothing at all"));
    }

    [Fact]
    public async Task StreamAsync_ShouldNeverConnectTheTradingSocketItself()
    {
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.ConnectTradingCalls.Should().Be(0);
    }

    [Fact]
    public void StreamAsync_ShouldThrow_WhenAnAccountBelongsToAnotherVenue()
    {
        // Account handles are bare integers that collide across venues, so a projectx:9001 reaching this adapter must
        // never be subscribed as TRADOVATE account 9001. Eager, at the call -- not on the first read.
        TradovateAccountEventStream stream = CreateStream();

        Action subscribe = () => stream.StreamAsync([VenueAccountId.Create(ProjectX, "9001")], CancellationToken.None);

        subscribe.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Order and position events
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task StreamAsync_ShouldEmitAnOrderStateEvent_WhenAnOrderArrivesForASubscribedAccount()
    {
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseOrder(Order(id: 5150, account: 9001, ClientModels.OrderStatus.Canceled));

        OrderStateEvent state = (await reader.NextAsync()).Should().BeOfType<OrderStateEvent>().Subject;
        state.Account.Should().Be(Account(9001));
        state.VenueOrderKey.Should().Be("5150");
        state.State.Should().Be(VenueOrderState.Cancelled);
    }

    [Fact]
    public async Task StreamAsync_ShouldIgnoreAnOrder_WhenItsAccountIsNotSubscribed()
    {
        // One Tradovate login syncs EVERY account the user holds -- unlike ProjectX, which subscribes per account --
        // so the filter is this adapter's, and without it another account's orders reach this process's journal.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseOrder(Order(id: 5150, account: 8002, ClientModels.OrderStatus.Canceled));
        _webSocket.RaiseOrder(Order(id: 5151, account: 9001, ClientModels.OrderStatus.Canceled));

        // The 9001 order is the FIRST thing out: the 8002 one was dropped, not queued ahead of it.
        OrderStateEvent state = (await reader.NextAsync()).Should().BeOfType<OrderStateEvent>().Subject;
        state.VenueOrderKey.Should().Be("5151");
    }

    [Fact]
    public async Task StreamAsync_ShouldEmitAPositionEvent_KeepingTheSign()
    {
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));

        PositionEvent position = (await reader.NextAsync()).Should().BeOfType<PositionEvent>().Subject;
        position.NetQuantity.Should().Be(-3);
        position.Account.Should().Be(Account(9001));
        position.Contract.Should().Be(VenueContractId.Create(Tradovate, "222"));
    }

    [Fact]
    public async Task StreamAsync_ShouldIgnoreAPosition_WhenItsAccountIsNotSubscribed()
    {
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaisePosition(Position(account: 8002, net: -3, price: 5310m));
        _webSocket.RaisePosition(Position(account: 9001, net: 4, price: 5311m));

        PositionEvent position = (await reader.NextAsync()).Should().BeOfType<PositionEvent>().Subject;
        position.NetQuantity.Should().Be(4);
    }

    [Fact]
    public async Task StreamAsync_ShouldKeepStreaming_WhenOneEventCannotBeMapped()
    {
        // An open position with no netPrice is refused by the mapping (a fabricated 0 basis would feed a wrong
        // unrealised P&L to the R-5 gate). That refusal must cost ONE event, not the whole feed -- a stream that died
        // on a malformed payload would be the silence this seam exists to make impossible.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaisePosition(Position(account: 9001, net: 2, price: null));
        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));

        PositionEvent position = (await reader.NextAsync()).Should().BeOfType<PositionEvent>().Subject;
        position.NetQuantity.Should().Be(-3);
    }

    [Fact]
    public async Task StreamAsync_ShouldEmitAFlatPosition_EvenWhenItCarriesNoNetPrice()
    {
        // The flat retires live protection (OCO-cancel-on-exit, gh#183). Losing it leaves a resting safety stop
        // behind a position that no longer exists.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaisePosition(Position(account: 9001, net: 0, price: null));

        PositionEvent position = (await reader.NextAsync()).Should().BeOfType<PositionEvent>().Subject;
        position.NetQuantity.Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Fill attribution -- Tradovate's fill entity carries no account
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task StreamAsync_ShouldAttributeAFill_WhenItsOrderIsAlreadyKnown()
    {
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseOrder(Order(id: 5150, account: 9001, ClientModels.OrderStatus.Working));
        await reader.NextAsync(); // the order-state event

        _webSocket.RaiseFill(Fill(id: 77, order: 5150));

        FillEvent fill = (await reader.NextAsync()).Should().BeOfType<FillEvent>().Subject;
        fill.Account.Should().Be(Account(9001));
        fill.VenueOrderKey.Should().Be("5150");
        fill.VenueFillKey.Should().Be("77");
        fill.Quantity.Should().Be(2);
    }

    [Fact]
    public async Task StreamAsync_ShouldHoldAFillAndEmitIt_WhenItsOrderArrivesAfterIt()
    {
        // Nothing orders the props frames, so a fill can land before the order that names its account. Dropping it
        // would lose a real execution -- truth the journal cannot reconstruct -- so it is held until it can be named.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseFill(Fill(id: 77, order: 5150));
        _webSocket.RaiseOrder(Order(id: 5150, account: 9001, ClientModels.OrderStatus.Working));

        // The order-state event first (it is what unblocked the fill), then the fill that was held for it.
        (await reader.NextAsync()).Should().BeOfType<OrderStateEvent>();
        FillEvent fill = (await reader.NextAsync()).Should().BeOfType<FillEvent>().Subject;
        fill.Account.Should().Be(Account(9001));
        fill.VenueFillKey.Should().Be("77");
    }

    [Fact]
    public async Task StreamAsync_ShouldHoldEveryFillForAnOrder_WhenSeveralLandBeforeIt()
    {
        // A partial-fill sequence arrives as several fills against one order. Releasing only the first would lose the
        // rest of a real execution, and the journal has no way to reconstruct them.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseFill(Fill(id: 77, order: 5150));
        _webSocket.RaiseFill(Fill(id: 78, order: 5150));
        _webSocket.RaiseOrder(Order(id: 5150, account: 9001, ClientModels.OrderStatus.Working));

        (await reader.NextAsync()).Should().BeOfType<OrderStateEvent>();
        (await reader.NextAsync()).Should().BeOfType<FillEvent>().Subject.VenueFillKey.Should().Be("77");
        (await reader.NextAsync()).Should().BeOfType<FillEvent>().Subject.VenueFillKey.Should().Be("78");
    }

    [Fact]
    public async Task StreamAsync_ShouldAttributeAHeldFillFromTheSyncSnapshot_WhenNoOrderFrameEverArrives()
    {
        // The snapshot is the other source of the order → account map: on a reconnect the host re-syncs, and the
        // orders in that snapshot never arrive as props frames.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseFill(Fill(id: 77, order: 5150));
        _webSocket.RaiseSync(orders: [Order(id: 5150, account: 9001, ClientModels.OrderStatus.Working)]);

        FillEvent fill = (await reader.NextAsync()).Should().BeOfType<FillEvent>().Subject;
        fill.VenueFillKey.Should().Be("77");
        fill.Account.Should().Be(Account(9001));
    }

    [Fact]
    public async Task StreamAsync_ShouldNotEmitAFill_WhenItsOrderResolvesToAnUnsubscribedAccount()
    {
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseFill(Fill(id: 77, order: 5150));
        _webSocket.RaiseOrder(Order(id: 5150, account: 8002, ClientModels.OrderStatus.Working));
        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));

        // The held fill was resolved to a foreign account and discarded, not emitted under a fabricated one -- and
        // the foreign ORDER was filtered too, so the position is the first thing out.
        (await reader.NextAsync()).Should().BeOfType<PositionEvent>();
    }

    [Fact]
    public async Task StreamAsync_ShouldNotEmitAFillForAnUnsubscribedAccount_WhenItsOrderWasAlreadyKnown()
    {
        // The SAME filter as the test above, reached down the other path. A fill is attributed either immediately
        // (its order is already in the map) or after being held, and only one of those two routes is exercised by the
        // held-path test -- so a mutant that filtered on release but not on the immediate hit survived the suite until
        // this existed. What it guards is cross-account contamination: one Tradovate login syncs EVERY account the
        // user holds, so an unfiltered fill lands another account's execution in this process's journal.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseOrder(Order(id: 5150, account: 8002, ClientModels.OrderStatus.Working));
        _webSocket.RaiseFill(Fill(id: 77, order: 5150));
        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));

        (await reader.NextAsync()).Should().BeOfType<PositionEvent>();
    }

    [Fact]
    public async Task StreamAsync_ShouldNeverEmitAFillItCannotAttribute()
    {
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseFill(Fill(id: 77, order: 5150));
        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));

        // The position overtakes the unattributed fill rather than the fill being emitted under a guessed account.
        (await reader.NextAsync()).Should().BeOfType<PositionEvent>();
    }

    [Fact]
    public async Task StreamAsync_ShouldReportOnTeardown_WhenItStillHoldsAFillItCouldNeverAttribute()
    {
        // A held fill that is never named is a real execution the journal never receives. Nothing else records that,
        // so the teardown line is the whole trace -- and a trace nobody asserts on is not a trace.
        await using (Reader reader = Start(CreateStream(), [Account(9001)]))
        {
            _webSocket.RaiseFill(Fill(id: 77, order: 5150));
        }

        _log.Messages.Should().ContainMatch("*never attributed to an account*");
    }

    [Fact]
    public async Task StreamAsync_ShouldStopHoldingAFill_WhenItsOrderNamesAnUnsubscribedAccount()
    {
        // Reaches the reason the order → account map records EVERY account rather than only the subscribed ones: a
        // foreign order is what lets a fill held against it be resolved and discarded. Recording only subscribed
        // accounts would leave it held forever — reported at teardown as a lost execution that was never ours.
        await using (Reader reader = Start(CreateStream(), [Account(9001)]))
        {
            _webSocket.RaiseFill(Fill(id: 77, order: 5150));
            _webSocket.RaiseOrder(Order(id: 5150, account: 8002, ClientModels.OrderStatus.Working));
        }

        _log.Messages.Should().NotContainMatch("*never attributed to an account*");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The reconnect gap -- the snapshot's fills are the only record of what happened while the socket was down
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task StreamAsync_ShouldEmitTheSyncSnapshotsFills_BecauseNothingElseCarriesAFillFromTheOutage()
    {
        // Round-2 review, finding 1. A fill that lands while the socket is down exists ONLY in the next
        // user/syncrequest snapshot: live props frames carry changes from the sync point forward, and gh#193
        // reconciles POSITIONS, not fills. Dropping it means no Fill row, no Trade, and a real realized loss that
        // never reaches the R-5 governor, the R-9 window or the R-4 throttle -- the exact gap
        // RecordUnmatchedFillAsync exists to refuse. Unlike a position, a fill is idempotent by construction
        // downstream (the unique index on { OrderId, VenueFillKey }), so re-delivering one costs a skip.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseSync(
            orders: [Order(id: 5150, account: 9001, ClientModels.OrderStatus.Working)],
            fills: [Fill(id: 77, order: 5150)]);

        FillEvent fill = (await reader.NextAsync()).Should().BeOfType<FillEvent>().Subject;
        fill.VenueFillKey.Should().Be("77");
        fill.Account.Should().Be(Account(9001));
    }

    [Fact]
    public async Task StreamAsync_ShouldDeliverAFillThatExecutedWhileTheSocketWasDown_OnTheReSubscribedStream()
    {
        // The failure in the shape it actually happens, end to end across the reconnect boundary -- not a snapshot
        // raised at a convenient moment on a live stream.
        //
        // A working order rests; the socket drops; the venue fills the order while this process is blind; the
        // connection host reconnects and re-syncs. That fill arrives ONLY in SyncResult.Fills -- a props frame will
        // never carry it, because props carries changes from the sync point forward, which is the entire reason the
        // snapshot exists. Dropping it means no Fill row, no composed Trade, and a realized loss that never reaches
        // the R-5 governor, the R-9 window or the R-4 throttle: they then read headroom that is not there and permit
        // risk against a position the operator actually took. Nothing looks wrong -- there is simply less in the
        // ledger than in the world.
        TradovateAccountEventStream stream = CreateStream();

        Func<Task> whileConnected = async () =>
        {
            await using Reader first = Start(stream, [Account(9001)]);
            _webSocket.RaiseOrder(Order(id: 5150, account: 9001, ClientModels.OrderStatus.Working));
            (await first.NextAsync()).Should().BeOfType<OrderStateEvent>();

            _webSocket.TradingState = ClientModels.ConnectionState.Disconnected;
            _webSocket.RaiseStatus(isTrading: true, ClientModels.ConnectionState.Disconnected);
            await first.NextAsync();
        };

        await whileConnected.Should().ThrowAsync<TradovateVenueException>();

        // The host brings the socket back and syncs it; the supervisor re-subscribes. The re-subscribed stream starts
        // with an EMPTY attribution map, so the snapshot has to supply both the order and the fill.
        //
        // The reconnect is driven through the SYNC REGISTER as well as the socket state (gh#1051 review), because
        // production cannot produce any other order: a new connection re-arms the obligation, so the socket really
        // is Connected-and-unsynced at the moment the supervisor re-subscribes, and the snapshot lands afterwards.
        // Setting only TradingState left the register synced from construction and quietly arranged a world the
        // code can no longer reach -- so this test, whose name asserts exactly the behaviour a refusal here would
        // remove, would have stayed green while that behaviour was deleted.
        _webSocket.TradingState = ClientModels.ConnectionState.Connected;
        _sync.OnSocketConnected();
        _sync.IsSynced.Should().BeFalse("a fresh connection carries no entity subscription until something syncs it");
        await using Reader resubscribed = Start(stream, [Account(9001)]);

        _webSocket.RaiseSync(
            orders: [Order(id: 5150, account: 9001, ClientModels.OrderStatus.Filled)],
            fills: [Fill(id: 77, order: 5150)]);

        FillEvent fill = (await resubscribed.NextAsync()).Should().BeOfType<FillEvent>().Subject;
        fill.VenueFillKey.Should().Be("77");
        fill.VenueOrderKey.Should().Be("5150");
        fill.Account.Should().Be(Account(9001));
        fill.Quantity.Should().Be(2);
        fill.ExecutionPrice.Should().Be(new Price(5312.25m));
    }

    [Fact]
    public async Task StreamAsync_ShouldKeyAReplayedSnapshotFillIdentically_SoTheDedupeDownstreamHolds()
    {
        // Emitting the snapshot's fills is only safe because a replay dedupes downstream, and that dedupe is the
        // unique { OrderId, VenueFillKey } index -- so both halves of the key have to be the venue's OWN immutable
        // ids, never a composite derived from mutable fields. This repo has been bitten by exactly that: a FIFO
        // pairing key re-derived on a late fill made exact-key dedup double-count. Two syncs re-deliver one fill;
        // the keys must be byte-identical, or the "replay is a skip" argument is false and the R-5 inputs
        // double-count instead.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseSync(
            orders: [Order(id: 5150, account: 9001, ClientModels.OrderStatus.Working)],
            fills: [Fill(id: 77, order: 5150)]);
        FillEvent first = (await reader.NextAsync()).Should().BeOfType<FillEvent>().Subject;

        // A second sync -- the host sends one after every connect it drives -- re-delivers the same fill.
        _webSocket.RaiseSync(
            orders: [Order(id: 5150, account: 9001, ClientModels.OrderStatus.Filled)],
            fills: [Fill(id: 77, order: 5150)]);
        FillEvent replay = (await reader.NextAsync()).Should().BeOfType<FillEvent>().Subject;

        replay.VenueFillKey.Should().Be(first.VenueFillKey);
        replay.VenueOrderKey.Should().Be(first.VenueOrderKey);
        replay.Account.Should().Be(first.Account);
    }

    [Fact]
    public async Task StreamAsync_ShouldStillEmitNoOrderOrPositionFromTheSnapshot_WhenItEmitsItsFills()
    {
        // The carve-out is fills ONLY, and the asymmetry is the whole point. A snapshot POSITION re-drives the
        // OCO-exit retire and the round-trip journal -- ProcessFlatAsync composes a Trade, so a re-delivered flat
        // double-counts realized P&L into the R-5 governor. Fills cannot: they dedupe on a unique index.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseSync(
            orders: [Order(id: 5150, account: 9001, ClientModels.OrderStatus.Canceled)],
            positions: [Position(account: 9001, net: 0, price: null)],
            fills: [Fill(id: 77, order: 5150)]);

        // The fill, and then nothing else from the snapshot -- the next LIVE event follows it directly.
        (await reader.NextAsync()).Should().BeOfType<FillEvent>();
        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));
        (await reader.NextAsync()).Should().BeOfType<PositionEvent>().Subject.NetQuantity.Should().Be(-3);
    }

    [Fact]
    public async Task StreamAsync_ShouldHoldASnapshotFill_WhenTheSnapshotDoesNotNameItsOrder()
    {
        // A snapshot fill is attributed by the same rule as a live one, so a snapshot carrying the fill but not the
        // order still must not guess an account.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseSync(fills: [Fill(id: 77, order: 5150)]);
        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));

        (await reader.NextAsync()).Should().BeOfType<PositionEvent>();
    }

    [Fact]
    public async Task StreamAsync_ShouldNotEmitASnapshotFill_ForAnUnsubscribedAccount()
    {
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseSync(
            orders: [Order(id: 5150, account: 8002, ClientModels.OrderStatus.Working)],
            fills: [Fill(id: 77, order: 5150)]);
        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));

        (await reader.NextAsync()).Should().BeOfType<PositionEvent>();
    }

    // ---------------------------------------------------------------------------------------------------------
    // The hold buffer's cap -- the one branch that knowingly discards a real execution
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task StreamAsync_ShouldEvictTheOldestHeldFill_WhenTheHoldBufferIsFull()
    {
        // Round-2 review, finding 3. This branch loses a real execution on purpose, to bound the buffer, and it was
        // the one branch with no test at all. Fill 1000 is the oldest of 513, so it is the one evicted -- and the log
        // line naming it is the only trace that an execution was dropped.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        for (int i = 0; i <= HoldCap; i++)
        {
            _webSocket.RaiseFill(Fill(id: 1000 + i, order: 5000 + i));
        }

        _log.Messages.Should().ContainMatch("*dropping the oldest*");
        _log.Messages.Should().ContainMatch("*fill 1000 on order 5000*", "the OLDEST held fill is the one evicted");
    }

    [Fact]
    public async Task StreamAsync_ShouldNeverEmitTheEvictedFill_WhenItsOrderFinallyArrives()
    {
        // Round-2 review, and the pattern behind it: every eviction assertion in this file had been written against
        // what SURVIVES, and a survivor assertion is satisfiable by discarding something else. `RemoveLast()` passed
        // all 38 tests -- it drops fill 1511, which no survivor assertion happened to name.
        //
        // A discard is only pinned by asserting on what is GONE. Order 5000 names the evicted fill 1000; nothing
        // must come out for it. The position after it is the marker that proves the absence rather than a race:
        // if fill 1000 were still held, it would be released ahead of the position.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        for (int i = 0; i <= HoldCap; i++)
        {
            _webSocket.RaiseFill(Fill(id: 1000 + i, order: 5000 + i));
        }

        _webSocket.RaiseOrder(Order(id: 5000, account: 9001, ClientModels.OrderStatus.Working));
        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));

        (await reader.NextAsync()).Should().BeOfType<OrderStateEvent>();
        (await reader.NextAsync()).Should().BeOfType<PositionEvent>(
            "fill 1000 was evicted, so its order arriving must release nothing");
    }

    [Fact]
    public async Task StreamAsync_ShouldNotEvictAnything_WhileTheHoldBufferIsWithinItsCap()
    {
        // The paired half: an eviction that fired early would silently discard executions that were never at risk.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        for (int i = 0; i < HoldCap; i++)
        {
            _webSocket.RaiseFill(Fill(id: 1000 + i, order: 5000 + i));
        }

        _log.Messages.Should().NotContainMatch("*dropping the oldest*");
    }

    [Fact]
    public async Task StreamAsync_ShouldStillReleaseTheSurvivingHeldFills_AfterAnEviction()
    {
        // The other half of the pair: the test above pins what is GONE, this one pins that nothing ELSE went with it.
        // Neither alone is enough -- a survivor assertion is satisfiable by discarding something it does not name
        // (which is how `RemoveLast()` survived), and a gone-assertion alone is satisfiable by discarding the whole
        // buffer. Both ends of the surviving range are checked, because a middle survivor is what a `Clear()` mutant
        // takes and the newest is what it cannot (it is added after the eviction).
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        for (int i = 0; i <= HoldCap; i++)
        {
            _webSocket.RaiseFill(Fill(id: 1000 + i, order: 5000 + i));
        }

        // A fill from the MIDDLE of the buffer -- not the newest, which survives even a mutation that clears the
        // whole buffer, because it is added after the eviction. Only a survivor from behind the evicted head proves
        // that exactly one fill was dropped.
        _webSocket.RaiseOrder(Order(id: 5001, account: 9001, ClientModels.OrderStatus.Working));

        (await reader.NextAsync()).Should().BeOfType<OrderStateEvent>();
        (await reader.NextAsync()).Should().BeOfType<FillEvent>().Subject.VenueFillKey.Should().Be("1001");

        // ...and the newest, so both ends of the surviving range are covered.
        _webSocket.RaiseOrder(Order(id: 5000 + HoldCap, account: 9001, ClientModels.OrderStatus.Working));

        (await reader.NextAsync()).Should().BeOfType<OrderStateEvent>();
        (await reader.NextAsync()).Should().BeOfType<FillEvent>()
            .Subject.VenueFillKey.Should().Be((1000 + HoldCap).ToString(CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Silence -- a socket that drops must end the stream, not sit open and quiet
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task StreamAsync_ShouldEndTheStream_WhenTheTradingSocketDrops()
    {
        // A dropped socket delivers nothing further, and the client's replay covers only its OWN reconnect. An open
        // sequence over a dead socket is indistinguishable from a quiet account, so end it and let the supervisor
        // re-subscribe over the socket the connection host brings back and re-syncs.
        TradovateAccountEventStream stream = CreateStream();

        Func<Task> read = async () =>
        {
            await using Reader reader = Start(stream, [Account(9001)]);
            _webSocket.RaiseStatus(isTrading: true, ClientModels.ConnectionState.Disconnected);
            await reader.NextAsync();
        };

        await read.Should().ThrowAsync<TradovateVenueException>();
    }

    [Fact]
    public async Task StreamAsync_ShouldNameTheFillsItWasStillHolding_WhenTheDropEndsTheStream()
    {
        // This is what the drain-then-throw shape buys, and the only thing it buys: the count of fills the stream was
        // still holding -- executions it will now never attribute -- is knowable only once the drain is over. An
        // exception built in the drop handler, before the drain, could not carry it. Completing the channel WITH the
        // error is otherwise indistinguishable (buffered events survive it and the exception is rethrown unwrapped),
        // so without this assertion the two shapes are the same test.
        TradovateAccountEventStream stream = CreateStream();

        Func<Task> read = async () =>
        {
            await using Reader reader = Start(stream, [Account(9001)]);
            _webSocket.RaiseFill(Fill(id: 77, order: 5150));
            _webSocket.RaiseStatus(isTrading: true, ClientModels.ConnectionState.Disconnected);
            await reader.NextAsync();
        };

        (await read.Should().ThrowAsync<TradovateVenueException>())
            .WithMessage("*1 fill(s) still awaiting*");
    }

    [Fact]
    public async Task StreamAsync_ShouldEndTheStream_WhenTheSocketDropsBetweenTheConnectedCheckAndTheAttach()
    {
        // Round-2 review, finding 2. The connected check and the handler attach are not one step, and a drop landing
        // between them is raised to nobody: the ConnectionStatusChanged handler is not attached yet, so the drop is
        // never seen, the channel is never completed, and the read parks FOREVER on a dead socket. The supervisor has
        // no watchdog -- it only reacts to a session that ends or throws -- so nothing above would ever notice. That
        // is precisely the "open sequence over a dead socket" this class exists to prevent (R-13, ADR-0019).
        _webSocket.WhenOrderHandlerAttached = () =>
            _webSocket.TradingState = ClientModels.ConnectionState.Disconnected;
        TradovateAccountEventStream stream = CreateStream();

        Func<Task> read = async () =>
        {
            await using Reader reader = Start(stream, [Account(9001)]);
            await reader.NextAsync();
        };

        await read.Should().ThrowAsync<TradovateVenueException>();
    }

    [Fact]
    public async Task StreamAsync_ShouldDeliverAlreadyBufferedEvents_BeforeItReportsTheDrop()
    {
        // Events the socket delivered before it dropped are still truth. Losing them to the teardown would be the
        // same silence, one layer down.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));
        _webSocket.RaiseStatus(isTrading: true, ClientModels.ConnectionState.Disconnected);

        (await reader.NextAsync()).Should().BeOfType<PositionEvent>();
        Func<Task> next = () => reader.NextAsync();
        await next.Should().ThrowAsync<TradovateVenueException>();
    }

    [Fact]
    public async Task StreamAsync_ShouldNotEndTheStream_WhenTheMarketDataSocketDrops()
    {
        // Reaches the IsTradingSocket half of the guard specifically: one socket carries quotes and the other carries
        // entities, and a quote-feed blip must not tear down the account stream.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseStatus(isTrading: false, ClientModels.ConnectionState.Disconnected);
        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));

        (await reader.NextAsync()).Should().BeOfType<PositionEvent>();
    }

    [Fact]
    public async Task StreamAsync_ShouldNotEndTheStream_WhenTheTradingSocketReportsConnected()
    {
        // Reaches the "left Connected" half of the same guard, separately from the IsTradingSocket half above. A
        // transition INTO Connected is the connection host re-arming its sync, never a reason to drop the stream.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.RaiseStatus(isTrading: true, ClientModels.ConnectionState.Connected);
        _webSocket.RaisePosition(Position(account: 9001, net: -3, price: 5310m));

        (await reader.NextAsync()).Should().BeOfType<PositionEvent>();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Teardown
    // ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task StreamAsync_ShouldAttachEveryHandlerItNeeds_WhileTheStreamIsLive()
    {
        // The paired half of the detach guard below: a detach test alone passes trivially if nothing ever attached.
        await using Reader reader = Start(CreateStream(), [Account(9001)]);

        _webSocket.OrderSubscribers.Should().Be(1);
        _webSocket.FillSubscribers.Should().Be(1);
        _webSocket.PositionSubscribers.Should().Be(1);
        _webSocket.SyncSubscribers.Should().Be(1);
        _webSocket.StatusSubscribers.Should().Be(1);
    }

    [Fact]
    public async Task StreamAsync_ShouldDetachEveryHandler_WhenTheStreamIsTornDown()
    {
        // A leaked handler keeps mapping payloads into a channel nobody reads, for the life of the process -- and it
        // holds the whole attribution map alive with it.
        Reader reader = Start(CreateStream(), [Account(9001)]);
        await reader.DisposeAsync();

        _webSocket.OrderSubscribers.Should().Be(0);
        _webSocket.FillSubscribers.Should().Be(0);
        _webSocket.PositionSubscribers.Should().Be(0);
        _webSocket.SyncSubscribers.Should().Be(0);
        _webSocket.StatusSubscribers.Should().Be(0);
    }

    [Fact]
    public async Task StreamAsync_ShouldDetachEveryHandler_WhenTheSocketDropEndsTheStream()
    {
        // The drop path exits through a THROW rather than a dispose, and that is the exit a live process actually
        // takes -- so it needs its own guard, not the ordinary teardown's.
        TradovateAccountEventStream stream = CreateStream();

        Func<Task> read = async () =>
        {
            await using Reader reader = Start(stream, [Account(9001)]);
            _webSocket.RaiseStatus(isTrading: true, ClientModels.ConnectionState.Disconnected);
            await reader.NextAsync();
        };

        await read.Should().ThrowAsync<TradovateVenueException>();
        _webSocket.OrderSubscribers.Should().Be(0);
        _webSocket.FillSubscribers.Should().Be(0);
        _webSocket.PositionSubscribers.Should().Be(0);
        _webSocket.SyncSubscribers.Should().Be(0);
        _webSocket.StatusSubscribers.Should().Be(0);
    }

    [Fact]
    public async Task StreamAsync_ShouldStopCleanly_WhenTheCallerCancels()
    {
        using CancellationTokenSource cancellation = new();
        TradovateAccountEventStream stream = CreateStream();

        Func<Task> read = async () =>
        {
            await using IAsyncEnumerator<AccountEvent> enumerator =
                stream.StreamAsync([Account(9001)], cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            Task<bool> move = enumerator.MoveNextAsync().AsTask();
            await cancellation.CancelAsync();
            await move.WaitAsync(Timeout);
        };

        await read.Should().ThrowAsync<OperationCanceledException>();
        _webSocket.OrderSubscribers.Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------------

    // The iterator body -- the connected-state guard, the channel, and the handler attach -- runs synchronously on
    // the first MoveNext, up to the point where it parks on the empty channel. So the enumerator has to be primed
    // before any event is raised, and that first move is the one the first NextAsync awaits.
    private static Reader Start(TradovateAccountEventStream stream, IReadOnlyCollection<VenueAccountId> accounts)
    {
        CancellationTokenSource stop = new();
        IAsyncEnumerator<AccountEvent> enumerator =
            stream.StreamAsync(accounts, stop.Token).GetAsyncEnumerator(stop.Token);

        return new Reader(enumerator, enumerator.MoveNextAsync().AsTask(), stop);
    }

    private sealed class Reader(
        IAsyncEnumerator<AccountEvent> enumerator, Task<bool> primed, CancellationTokenSource stop) : IAsyncDisposable
    {
        private Task<bool>? _pending = primed;

        public async Task<AccountEvent> NextAsync()
        {
            Task<bool> move = _pending ?? enumerator.MoveNextAsync().AsTask();
            _pending = null;

            // Bounded: an unbounded await here would hang the whole xunit run and read as slow CI, not a red test.
            (await move.WaitAsync(Timeout)).Should().BeTrue("the stream should have produced an event");
            return enumerator.Current;
        }

        public async ValueTask DisposeAsync()
        {
            // The parked MoveNext has to END before the enumerator is disposed -- disposing one with a move in flight
            // throws NotSupportedException -- so cancel the stream and let the iterator run its own teardown, which
            // is the path a shutdown takes anyway.
            if (_pending is { } pending)
            {
                _pending = null;
                await stop.CancelAsync();
                try
                {
                    await pending.WaitAsync(Timeout);
                }
                catch (OperationCanceledException)
                {
                    // a clean stop
                }
                catch (TradovateVenueException)
                {
                    // the drop the test under way is asserting on
                }
            }

            await enumerator.DisposeAsync();
            stop.Dispose();
        }
    }

    private static VenueAccountId Account(long id) =>
        VenueAccountId.Create(Tradovate, id.ToString(CultureInfo.InvariantCulture));

    private static ClientModels.Order Order(long? id, long account, ClientModels.OrderStatus status) => new()
    {
        Id = id,
        AccountId = account,
        ContractId = 222,
        Timestamp = At,
        Action = ClientModels.OrderAction.Buy,
        OrdStatus = status,
        Admin = false,
    };

    private static ClientModels.Fill Fill(long? id, long order) => new()
    {
        Id = id,
        OrderId = order,
        ContractId = 222,
        Timestamp = At,
        TradeDate = new ClientModels.TradeDate { Year = 2026, Month = 3, Day = 4 },
        Action = ClientModels.OrderAction.Buy,
        Qty = 2,
        Price = 5312.25m,
        Active = true,
        FinallyPaired = 0,
    };

    private static ClientModels.Position Position(long account, int net, decimal? price) => new()
    {
        Id = 11,
        AccountId = account,
        ContractId = 222,
        Timestamp = At,
        TradeDate = new ClientModels.TradeDate { Year = 2026, Month = 3, Day = 4 },
        NetPos = net,
        NetPrice = price,
        Bought = 2,
        BoughtValue = 10624.50m,
        Sold = 0,
        SoldValue = 0m,
        PrevPos = 0,
    };

    /// <summary>Captures log lines, so the cases whose only trace is a log line can still be asserted on.</summary>
    /// <summary>
    /// Records what was logged <b>and at what level</b>. The level is not decoration: a double that discarded it
    /// would let a <c>LogError</c> silently downgraded to <c>LogDebug</c> keep every assertion here green, which is
    /// a defect this repository has already shipped once and paid for.
    /// </summary>
    private sealed class CapturingLogger : ILogger<TradovateAccountEventStream>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        /// <summary>Everything logged, at any level.</summary>
        public IEnumerable<string> Messages => _entries.Select(entry => entry.Message);

        /// <summary>Only what was logged at <see cref="LogLevel.Warning"/>.</summary>
        public IEnumerable<string> Warnings => At(LogLevel.Warning);

        /// <summary>Only what was logged at <see cref="LogLevel.Error"/>.</summary>
        public IEnumerable<string> Errors => At(LogLevel.Error);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_entries)
            {
                _entries.Add((level, formatter(state, error)));
            }
        }

        private IEnumerable<string> At(LogLevel level)
        {
            lock (_entries)
            {
                return [.. _entries.Where(entry => entry.Level == level).Select(entry => entry.Message)];
            }
        }
    }

    /// <summary>
    /// A hand-rolled socket double that <b>counts its event subscribers</b>. That count is the whole reason it exists:
    /// a leaked handler is invisible through a fake — it writes into an abandoned channel, throws nothing, and changes
    /// no output — so the teardown guards could not fail without it.
    /// </summary>
    private sealed class RecordingWebSocketClient : ITradovateWebSocketClient
    {
        public ClientModels.ConnectionState TradingState { get; set; } = ClientModels.ConnectionState.Connected;

        public ClientModels.ConnectionState MarketDataState { get; set; } = ClientModels.ConnectionState.Connected;

        public int ConnectTradingCalls { get; private set; }

        public int OrderSubscribers => _orderReceived?.GetInvocationList().Length ?? 0;

        public int FillSubscribers => FillReceived?.GetInvocationList().Length ?? 0;

        public int PositionSubscribers => PositionReceived?.GetInvocationList().Length ?? 0;

        public int SyncSubscribers => SyncCompleted?.GetInvocationList().Length ?? 0;

        public int StatusSubscribers => ConnectionStatusChanged?.GetInvocationList().Length ?? 0;

        public event EventHandler<ClientModels.ConnectionStatusChange>? ConnectionStatusChanged;

        public event EventHandler<ClientModels.WebSocketMessageFailedEventArgs>? MessageSendFailed;

        public event EventHandler<ClientModels.SyncResult>? SyncCompleted;

        public event EventHandler<ClientModels.EntityPropsEvent>? EntityReceived;

        /// <summary>Runs when the stream attaches its order handler — the attach window the drop race lives in.</summary>
        public Action? WhenOrderHandlerAttached { get; set; }

        private EventHandler<ClientModels.Order>? _orderReceived;

        public event EventHandler<ClientModels.Order>? OrderReceived
        {
            add
            {
                _orderReceived += value;
                WhenOrderHandlerAttached?.Invoke();
            }

            remove => _orderReceived -= value;
        }

        public event EventHandler<ClientModels.Position>? PositionReceived;

        public event EventHandler<ClientModels.Fill>? FillReceived;

        public event EventHandler<ClientModels.CashBalance>? CashBalanceReceived;

        public event EventHandler<ClientModels.Quote>? QuoteReceived;

        public event EventHandler<ClientModels.DomBook>? DomReceived;

        public event EventHandler<IReadOnlyList<ClientModels.ChartBar>>? ChartBarsReceived;

        public void RaiseOrder(ClientModels.Order order) => _orderReceived?.Invoke(this, order);

        public void RaiseFill(ClientModels.Fill fill) => FillReceived?.Invoke(this, fill);

        public void RaisePosition(ClientModels.Position position) => PositionReceived?.Invoke(this, position);

        public void RaiseSync(
            IReadOnlyList<ClientModels.Order>? orders = null,
            IReadOnlyList<ClientModels.Position>? positions = null,
            IReadOnlyList<ClientModels.Fill>? fills = null) =>
            SyncCompleted?.Invoke(this, new ClientModels.SyncResult
            {
                Orders = orders ?? [],
                Positions = positions ?? [],
                Fills = fills ?? [],
            });

        public void RaiseStatus(bool isTrading, ClientModels.ConnectionState current) =>
            ConnectionStatusChanged?.Invoke(this, new ClientModels.ConnectionStatusChange
            {
                IsTradingSocket = isTrading,
                Previous = ClientModels.ConnectionState.Connected,
                Current = current,
            });

        public Task ConnectTradingAsync(CancellationToken cancellationToken = default)
        {
            ConnectTradingCalls++;
            return Task.CompletedTask;
        }

        public Task ConnectMarketDataAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectTradingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectMarketDataAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ClientModels.SyncResult> SyncRequestAsync(
            ClientModels.SyncRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClientModels.SyncResult());

        public Task SubscribeQuoteAsync(string symbolOrContractId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UnsubscribeQuoteAsync(string symbolOrContractId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SubscribeDomAsync(string symbolOrContractId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UnsubscribeDomAsync(string symbolOrContractId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SubscribeChartAsync(
            ClientModels.ChartRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ClientModels.ChartBar>> GetHistoricalBarsAsync(
            ClientModels.ChartRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClientModels.ChartBar>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // Declared by the interface and unused by this seam; referenced here so the compiler does not warn them away.
        internal void Unused() => _ = (MessageSendFailed, EntityReceived, CashBalanceReceived, QuoteReceived, DomReceived, ChartBarsReceived);
    }
}
