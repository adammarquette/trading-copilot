using System.Reflection;
using FakeItEasy;
using FluentAssertions;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Recovery;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.MarketData;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Realtime;

public class RealtimeHubTests
{
    private static readonly DateTimeOffset _at = DateTimeOffset.UnixEpoch.AddYears(56);

    private readonly IEventLog _eventLog = A.Fake<IEventLog>();
    private readonly IClientProxy _caller = A.Fake<IClientProxy>();

    private RealtimeHub Hub() => new(_eventLog, A.Fake<ILogger<RealtimeHub>>());

    private static EventEnvelope Event(long sequence, string type) =>
        new(Guid.NewGuid(), sequence, type, "test", _at.AddSeconds(sequence), _at.AddSeconds(sequence), "{}", null);

    private void PageAfter(long afterSequence, EventPage page) =>
        A.CallTo(() => _eventLog.ReadAfterAsync(afterSequence, A<int>._, A<CancellationToken>._)).Returns(page);

    private bool SentEvent(long sequence) => Fake.GetCalls(_caller).Any(call =>
        call.Method.Name == nameof(IClientProxy.SendCoreAsync)
        && (string)call.Arguments[0]! == RealtimeEvent.ClientMethod
        && ((object?[])call.Arguments[1]!)[0] is RealtimeEvent e && e.Sequence == sequence);

    private int GapNotices() => Fake.GetCalls(_caller).Count(call =>
        call.Method.Name == nameof(IClientProxy.SendCoreAsync)
        && (string)call.Arguments[0]! == RealtimeGap.ClientMethod);

    // ---- resume-cursor parsing ----

    [Theory]
    [InlineData("5", true, 5)]
    [InlineData("0", true, 0)]
    [InlineData(null, false, 0)]
    [InlineData("", false, 0)]
    [InlineData("not-a-number", false, 0)]
    [InlineData("-1", false, 0)] // a negative cursor is nonsense; treat as no resume, not "from the start"
    public void TryParseResumeAfter_ShouldParseOnlyANonNegativeInteger(string? raw, bool expected, long expectedAfter)
    {
        RealtimeHub.TryParseResumeAfter(raw, out long after).Should().Be(expected);
        after.Should().Be(expectedAfter);
    }

    // ---- backlog replay (idempotent resume) ----

    [Fact]
    public async Task ReplayBacklogAsync_ShouldReplayBroadcastEventsAfterTheCursor_AndSkipNonBroadcastTypes()
    {
        PageAfter(0, EventPage.Of(
        [
            Event(1, QuoteIngestionService.QuoteEventType),
            Event(2, "internal.not-broadcast"), // not on the allow-list: never leaves the server
            Event(3, KillSwitchService.EngagedEventType),
        ]));

        await Hub().ReplayBacklogAsync(afterSequence: 0, _caller, CancellationToken.None);

        SentEvent(1).Should().BeTrue();
        SentEvent(3).Should().BeTrue();
        SentEvent(2).Should().BeFalse("a type not in RealtimeEventCatalog is never forwarded to a client");
    }

    [Fact]
    public async Task ReplayBacklogAsync_ShouldTellTheClientToRefetch_WhenTheCursorFellOffTheRetentionWindow()
    {
        PageAfter(10, new EventPage(
            [Event(11, QuoteIngestionService.QuoteEventType)],
            new EventRetentionGap(RequestedAfterSequence: 10, OldestAvailableSequence: 11, _at)));

        await Hub().ReplayBacklogAsync(afterSequence: 10, _caller, CancellationToken.None);

        GapNotices().Should().Be(1, "a retention gap is reported, never silently skipped");
        SentEvent(11).Should().BeTrue("the tail the log did return still replays");
    }

    [Fact]
    public async Task ReplayBacklogAsync_ShouldBoundTheReplay_AndReportAGap_WhenTheClientIsTooFarBehind()
    {
        PageAfter(0, EventPage.Of(
        [
            Event(1, QuoteIngestionService.QuoteEventType),
            Event(2, QuoteIngestionService.QuoteEventType),
            Event(3, QuoteIngestionService.QuoteEventType),
        ]));

        // A resume from far behind must not stream the whole retention window; cap it and tell the client to refetch.
        await Hub().ReplayBacklogAsync(afterSequence: 0, _caller, CancellationToken.None, maxEvents: 2);

        SentEvent(1).Should().BeTrue();
        SentEvent(2).Should().BeTrue();
        SentEvent(3).Should().BeFalse("the replay stopped at the cap");
        GapNotices().Should().Be(1);
    }

    [Fact]
    public async Task ReplayBacklogAsync_ShouldBoundByRowsExamined_NotJustBroadcastEvents()
    {
        // A long run of non-broadcast rows must still trip the cap — the resume bounds WORK done, not just sends,
        // so it can never scan the whole retention window on one connect.
        PageAfter(0, EventPage.Of(
        [
            Event(1, "internal.not-broadcast"),
            Event(2, "internal.not-broadcast"),
            Event(3, "internal.not-broadcast"),
            Event(4, QuoteIngestionService.QuoteEventType),
        ]));

        await Hub().ReplayBacklogAsync(afterSequence: 0, _caller, CancellationToken.None, maxEvents: 2);

        SentEvent(4).Should().BeFalse("the cap fires on rows examined, before the loop ever reaches the broadcast row");
        GapNotices().Should().Be(1);
    }

    [Fact]
    public async Task ReplayBacklogAsync_ShouldReplayNothing_ForAnEmptyBacklog()
    {
        PageAfter(99, EventPage.Of([]));

        await Hub().ReplayBacklogAsync(afterSequence: 99, _caller, CancellationToken.None);

        Fake.GetCalls(_caller).Should().BeEmpty();
    }

    // ---- the presentation-only guarantee ----

    [Fact]
    public void RealtimeHub_ShouldExposeNoInvocableMethod_SoItCanNeverBeACommandChannel()
    {
        // SignalR invokes public instance methods declared on a hub. The only one here is the OnConnectedAsync
        // lifecycle override; anything else would be a second path to state — this asserts there is none (R-18, ADR-0007).
        IEnumerable<string> invocable = typeof(RealtimeHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name);

        invocable.Should().BeEquivalentTo([nameof(RealtimeHub.OnConnectedAsync)]);
    }
}

public class RealtimeEventCatalogTests
{
    [Theory]
    [InlineData("market.quote")]
    [InlineData("market.context-trade")]
    [InlineData("killswitch.engaged")]
    [InlineData("killswitch.disengaged")]
    [InlineData("flatten.executed")]
    [InlineData("flatten.warning")]
    [InlineData("flatten.watchdog.critical")]
    // The degraded-protection pair (gh#222). Same class as the rest of the safety strip and on the same surface:
    // operator-wide, event-log-backed, single-operator deployment. Routing it through the per-owner seam instead
    // would make it the one strip signal travelling a different path -- and event-log rows are never IUserOwned.
    [InlineData("protection.orphaned")]
    [InlineData("protection.restored")]
    public void IsBroadcast_ShouldAllowTheMarketDataAndSafetyStripTypes(string type) =>
        RealtimeEventCatalog.IsBroadcast(type).Should().BeTrue();

    [Fact]
    public void IsBroadcast_ShouldAllowTheOrphanGuardsOwnConstants_SoTheCatalogCannotDriftFromTheProducer()
    {
        // Pinned against the producer's constants rather than string literals: the catalog and OrphanGuardService
        // are edited in different files, and a renamed event type would otherwise leave the alert silently
        // unbroadcast -- degraded protection that never reaches the operator, which is the failure this guards.
        RealtimeEventCatalog.IsBroadcast(OrphanGuardService.OrphanedEventType).Should().BeTrue();
        RealtimeEventCatalog.IsBroadcast(OrphanGuardService.RestoredEventType).Should().BeTrue();
    }

    [Theory]
    [InlineData("order.filled")] // owner-scoped: reaches clients via the gh#683 per-owner seam, never this hub
    [InlineData("suggestion.issued")] // owner-scoped: gh#684
    [InlineData("news")]
    [InlineData("")]
    public void IsBroadcast_ShouldRefuseAnythingNotOnTheAllowList(string type) =>
        RealtimeEventCatalog.IsBroadcast(type).Should().BeFalse();

    [Fact]
    public void BroadcastTypeCount_ShouldPinTheBoundary_SoAnAdditionIsADeliberateReviewedChange() =>
        // Market quote + context-trade (2) + kill-switch (3) + flatten (6) + flatten-watchdog (3)
        // + degraded protection (2, gh#222) = 16.
        RealtimeEventCatalog.BroadcastTypeCount.Should().Be(16);
}

public class RealtimeEventTests
{
    [Fact]
    public void From_ShouldProjectTheDurableEnvelopeOntoTheWireShape_DroppingTheServerSideId()
    {
        var envelope = new EventEnvelope(
            Guid.NewGuid(), Sequence: 42, "market.quote", "projectx",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "{\"bid\":1}", TraceParent: "trace");

        RealtimeEvent projected = RealtimeEvent.From(envelope);

        projected.Should().Be(new RealtimeEvent(42, "market.quote", DateTimeOffset.UnixEpoch, "{\"bid\":1}"));
    }

    [Fact]
    public void From_ShouldProjectARetentionGapOntoTheWireShape()
    {
        var gap = new EventRetentionGap(RequestedAfterSequence: 10, OldestAvailableSequence: 25, DateTimeOffset.UnixEpoch);

        RealtimeGap.From(gap).Should().Be(new RealtimeGap(10, 25, DateTimeOffset.UnixEpoch));
    }
}

/// <summary>
/// The fan-out's pure catch-up phase machine (gh#645, #690). It decides, per read pass, whether to announce a
/// catch-up start, whether to broadcast the page's events, whether to announce caught-up, and the next phase — so
/// a restart replays its outage backlog as bracketed HISTORY rather than as live signals, while a cold first start
/// still swallows the whole-log backlog silently. Pure, so the safety-relevant transitions are pinned here rather
/// than inside the hosted loop.
/// </summary>
public class RealtimeFanoutPlanTests
{
    private const int Batch = 256;

    private static FanoutPassPlan Plan(RealtimeFanoutPhase phase, int eventCount) =>
        RealtimeEventLogFanoutHost.PlanPass(phase, eventCount, Batch);

    [Fact]
    public void FirstStart_ShouldSwallowTheBacklogSilently_AndNeverBracket()
    {
        // A cold start is not an outage: the whole log is history. Catch up silently — no broadcast, no bracket —
        // so a historical kill-switch / auto-flatten is never pushed as a live safety banner on a first-ever start.
        Plan(RealtimeFanoutPhase.SilentCatchUp, Batch)
            .Should().Be(new FanoutPassPlan(false, false, false, RealtimeFanoutPhase.SilentCatchUp));

        Plan(RealtimeFanoutPhase.SilentCatchUp, 10) // a short page reaches head
            .Should().Be(new FanoutPassPlan(false, false, false, RealtimeFanoutPhase.Live));
    }

    [Fact]
    public void Restart_WithNoBacklog_ShouldGoLiveSilently_WithNoBracket() =>
        // A fast restart already at head has no outage to announce.
        Plan(RealtimeFanoutPhase.RestartPending, 0)
            .Should().Be(new FanoutPassPlan(false, false, false, RealtimeFanoutPhase.Live));

    [Fact]
    public void Restart_WithABacklogInOnePage_ShouldBracketTheReplayAndGoLive() =>
        // Announce the start, replay the backlog as history, announce caught-up — all when the first page reaches head.
        Plan(RealtimeFanoutPhase.RestartPending, 10)
            .Should().Be(new FanoutPassPlan(
                AnnounceCatchUpStart: true, BroadcastEvents: true, AnnounceCaughtUp: true, RealtimeFanoutPhase.Live));

    [Fact]
    public void Restart_WithABacklogSpanningPages_ShouldAnnounceStartThenReplayUntilHead()
    {
        // First full page: announce the start and replay as history, but head is not reached — no caught-up yet.
        Plan(RealtimeFanoutPhase.RestartPending, Batch)
            .Should().Be(new FanoutPassPlan(true, true, false, RealtimeFanoutPhase.AnnouncedCatchUp));

        // A middle full page: keep replaying history, still not at head, no bracket edge.
        Plan(RealtimeFanoutPhase.AnnouncedCatchUp, Batch)
            .Should().Be(new FanoutPassPlan(false, true, false, RealtimeFanoutPhase.AnnouncedCatchUp));

        // The final short page: replay the tail, announce caught-up, go live.
        Plan(RealtimeFanoutPhase.AnnouncedCatchUp, 3)
            .Should().Be(new FanoutPassPlan(false, true, true, RealtimeFanoutPhase.Live));
    }

    [Fact]
    public void AnnouncedCatchUp_DrainingExactlyAtABatchBoundary_ShouldAnnounceCaughtUpOnTheEmptyPage() =>
        // A backlog that is an exact multiple of the batch reaches head on the empty page after the last full one.
        Plan(RealtimeFanoutPhase.AnnouncedCatchUp, 0)
            .Should().Be(new FanoutPassPlan(false, true, true, RealtimeFanoutPhase.Live));

    [Fact]
    public void Live_ShouldBroadcastAndStayLive()
    {
        Plan(RealtimeFanoutPhase.Live, 5)
            .Should().Be(new FanoutPassPlan(false, true, false, RealtimeFanoutPhase.Live));
        Plan(RealtimeFanoutPhase.Live, 0)
            .Should().Be(new FanoutPassPlan(false, true, false, RealtimeFanoutPhase.Live));
    }
}
