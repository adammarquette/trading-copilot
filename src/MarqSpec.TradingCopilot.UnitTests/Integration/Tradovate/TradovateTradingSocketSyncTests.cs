using MarqSpec.TradingCopilot.Integration.Tradovate;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tradovate;

/// <summary>
/// The process-wide record of whether the Tradovate <b>trading</b> socket has actually been synced (gh#1051) — the
/// one fact that separates "the account is quiet" from "this socket was never subscribed to anything".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the state moved out of the connection host.</b> Tradovate pushes <c>props</c> entity frames only to a
/// socket that has completed <c>user/syncrequest</c>, so a socket that is open and authorized but unsynced is
/// connected and <i>permanently silent</i>, with no exception anywhere. The connection host knew that; nothing else
/// could. Keeping a second copy of the answer next to the host's own would be a guard that drifts, so there is one
/// home for it and both the host and <c>TradovateAccountEventStream</c> read it.
/// </para>
/// <para>
/// <b>The generation is what makes a late completion safe.</b> A sync response the client's receive loop has already
/// dispatched resolves whatever happens to the transport afterwards, so a completion can in principle arrive after a
/// fresh connect has re-armed the obligation — and clearing it there would leave the <i>new</i> connection
/// permanently unsynced. Every clear is therefore tied to the connection it was started on.
/// </para>
/// <para>
/// A completion the host did <b>not</b> send is the client's own reconnect syncing itself, and that one is safe to
/// take at face value: the client raises it from inside <c>ReconnectAsync</c>, still holding the connect gate that
/// every other transition into <c>Connected</c> must also take, so no newer connection can have interleaved. That is
/// read from the client's source, not assumed — it is why this type distinguishes the two at all.
/// </para>
/// </remarks>
public class TradovateTradingSocketSyncTests
{
    [Fact]
    public void IsSynced_ShouldBeFalse_BeforeAnythingHasSynced()
    {
        // Starts owing a sync rather than claiming one. A socket already Connected when this process starts was
        // synced by nothing it can see, and a possibly-duplicate snapshot is the right way to be wrong about that —
        // a silent trading socket is not.
        TradovateTradingSocketSync sync = new();

        sync.IsSynced.Should().BeFalse();
        sync.Obligation.Should().Be(TradovateSyncObligation.Pending);
    }

    [Fact]
    public void OnSocketConnected_ShouldArmTheGracePass_WhateverTheSocketOwedBefore()
    {
        // Any transition INTO Connected re-arms, whoever drove it: a fresh connection carries no entity subscription
        // until something syncs it, and the client's own reconnect skips its sync entirely on a null user id while
        // still reporting Connected.
        TradovateTradingSocketSync sync = new();
        sync.CompleteObservedSync().Should().BeTrue("the arrangement below only means anything from synced");
        sync.IsSynced.Should().BeTrue();

        sync.OnSocketConnected();

        sync.IsSynced.Should().BeFalse();
        sync.Obligation.Should().Be(TradovateSyncObligation.Pending);
    }

    [Fact]
    public void PromoteGraceToDue_ShouldPromoteOnlyFromPending()
    {
        // The grace pass exists so the client's own reconnect — which syncs in the statement after it reports
        // Connected — is not duplicated. Promoting from None would put a full snapshot on the ordinary path for
        // nothing.
        TradovateTradingSocketSync sync = new();

        sync.PromoteGraceToDue().Should().BeTrue();
        sync.Obligation.Should().Be(TradovateSyncObligation.Due);

        sync.PromoteGraceToDue().Should().BeFalse("Due is not Pending");
        sync.Obligation.Should().Be(TradovateSyncObligation.Due);
    }

    [Fact]
    public void RequireSync_ShouldMakeTheSyncDueWithNoGrace()
    {
        // A connect the HOST drove authorizes the socket and sends nothing else. Nothing in the process will ever
        // sync it otherwise, so there is nobody to be patient for.
        TradovateTradingSocketSync sync = new();

        sync.RequireSync();

        sync.Obligation.Should().Be(TradovateSyncObligation.Due);
    }

    [Fact]
    public void CompleteHostSync_ShouldClearTheObligation_WhenTheSyncWasStartedOnTheCurrentConnection()
    {
        TradovateTradingSocketSync sync = new();
        sync.RequireSync();
        long generation = sync.BeginHostSync();
        sync.EndHostSync();

        sync.CompleteHostSync(generation).Should().BeTrue();

        sync.IsSynced.Should().BeTrue();
        sync.Obligation.Should().Be(TradovateSyncObligation.None);
    }

    [Fact]
    public void CompleteHostSync_ShouldNotClearTheObligation_WhenTheSocketReconnectedWhileTheSyncWasInFlight()
    {
        // THE race gh#1051 records. The client fails every request still pending as it rebuilds the transport — but
        // a response the receive loop has ALREADY dispatched resolves regardless, so a sync started on the previous
        // connection can complete after a fresh one has re-armed. Clearing there would leave the new connection
        // connected, authorized and silent until the process restarts.
        TradovateTradingSocketSync sync = new();
        sync.RequireSync();
        long generation = sync.BeginHostSync();

        sync.OnSocketConnected(); // the transport was rebuilt underneath the in-flight sync
        sync.EndHostSync();

        sync.CompleteHostSync(generation).Should().BeFalse();
        sync.IsSynced.Should().BeFalse("the snapshot belongs to a connection that no longer exists");
        sync.Obligation.Should().Be(TradovateSyncObligation.Pending);
    }

    [Fact]
    public void CompleteHostSync_ShouldNotClearTheObligation_WhenTheNewConnectionOwesASyncOfItsOwn()
    {
        // The case that makes the generation load-bearing rather than decorative, and the one the test above cannot
        // reach. There, the reconnect left the obligation at Pending, so the clear's "from Due only" rule refused on
        // its own — the generation was never consulted. Here the host has already spent the grace pass on the NEW
        // connection, so the obligation is Due again and "from Due only" would happily clear it. Only the generation
        // can tell that this snapshot answers a question about a connection that no longer exists.
        //
        // Written after the mutation that removes the generation check survived this file's first pass.
        TradovateTradingSocketSync sync = new();
        sync.RequireSync();
        long generation = sync.BeginHostSync();

        sync.OnSocketConnected();          // the transport was rebuilt underneath the in-flight sync
        sync.PromoteGraceToDue();          // ...and the new connection has since been found unsynced
        sync.EndHostSync();

        sync.CompleteHostSync(generation).Should().BeFalse();
        sync.IsSynced.Should().BeFalse("the new connection has never been synced");
        sync.Obligation.Should().Be(TradovateSyncObligation.Due);
    }

    [Fact]
    public void CompleteHostSync_ShouldNotClearTheObligation_WhenNothingWasOwed()
    {
        // Clearing from None is harmless but clearing from Pending is not: a grace pass that a stale completion
        // cancels is a connection nothing ever syncs. Only an obligation this host actually took on is cleared.
        TradovateTradingSocketSync sync = new();
        long generation = sync.BeginHostSync();
        sync.EndHostSync();

        // Pending, never promoted to Due -- the host never decided to own this one.
        sync.CompleteHostSync(generation).Should().BeFalse();
        sync.Obligation.Should().Be(TradovateSyncObligation.Pending);
    }

    [Fact]
    public void CompleteObservedSync_ShouldClearTheObligation_WhenTheHostSentNoSyncOfItsOwn()
    {
        // The client's own reconnect syncing itself — the whole reason the host watches the event rather than only
        // tracking what it sent. It clears a PENDING obligation, which is what the grace pass is waiting for.
        TradovateTradingSocketSync sync = new();

        sync.CompleteObservedSync().Should().BeTrue();

        sync.IsSynced.Should().BeTrue();
    }

    [Fact]
    public void CompleteObservedSync_ShouldNotClearTheObligation_WhileAHostSyncIsInFlight()
    {
        // The event is raised from INSIDE SyncRequestAsync, before it returns, so a completion seen while the host
        // has a sync in flight is (all but certainly) the host's own — and that one is cleared by CompleteHostSync,
        // which is bound to the connection it was started on. Taking it here would throw that binding away.
        //
        // The cost when it really was the client's own sync racing the host's is one duplicate snapshot, which
        // consumers dedupe; the cost of the opposite mistake is a socket nothing ever syncs again.
        TradovateTradingSocketSync sync = new();
        sync.RequireSync();
        sync.BeginHostSync();

        sync.CompleteObservedSync().Should().BeFalse();

        sync.IsSynced.Should().BeFalse();
        sync.Obligation.Should().Be(TradovateSyncObligation.Due);
    }

    [Fact]
    public void BeginHostSync_ShouldReportANewGeneration_ForEachConnection()
    {
        // Two syncs started on two different connections must not be interchangeable, or the binding above is a
        // guard that cannot fail on the thing it names.
        TradovateTradingSocketSync sync = new();

        long first = sync.BeginHostSync();
        sync.EndHostSync();
        sync.OnSocketConnected();
        long second = sync.BeginHostSync();
        sync.EndHostSync();

        second.Should().NotBe(first);
    }

    [Fact]
    public void EndHostSync_ShouldReleaseTheInFlightMarker_SoALaterObservedSyncIsTakenAgain()
    {
        // Without the release, the FIRST host sync would suppress every later client-driven completion for the life
        // of the process, and a reconnect the client syncs itself would look unsynced forever.
        TradovateTradingSocketSync sync = new();
        sync.BeginHostSync();
        sync.EndHostSync();

        sync.CompleteObservedSync().Should().BeTrue();
    }
}
