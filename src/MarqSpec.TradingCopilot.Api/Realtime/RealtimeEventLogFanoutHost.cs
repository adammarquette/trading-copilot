using MarqSpec.TradingCopilot.Domain.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Realtime;

/// <summary>
/// Tails the event log and pushes each presentation signal live to every connected client over
/// <see cref="RealtimeHub"/> (gh#645, R-10). A read-only consumer of the log — it never appends, never mutates
/// state, and never touches the broker; it only fans events out. It follows the same hosted-consumer discipline as
/// <c>StopPromotionHost</c> (gh#153): a fresh DI scope per pass, a cursor committed per batch, and a clean exit on
/// the stop token — the last of which prevents the parallel-integration-suite <see cref="ObjectDisposedException"/>
/// cascade when the host provider is torn down before the token fires.
/// </summary>
/// <remarks>
/// <b>Restart catch-up (gh#645, #690).</b> A cold first start (no committed cursor) treats the whole log as history
/// and catches up to head <b>silently</b>. A restart (a committed cursor) instead <b>brackets</b> the outage backlog
/// it missed: a <see cref="RealtimeCatchUpPhase.Started"/>, the missed events replayed as <b>history</b>, then a
/// <see cref="RealtimeCatchUpPhase.Completed"/> and live from there — so a historical kill-switch / auto-flatten in
/// that backlog is never pushed as a <b>live</b> safety banner (the mistake the old restart path made by resuming
/// "live at once"). The decision per pass is the pure <see cref="PlanPass"/> phase machine, unit-tested apart from
/// the loop.
/// </remarks>
public sealed class RealtimeEventLogFanoutHost : BackgroundService
{
    /// <summary>This consumer's cursor group in the event log (ADR-0001). Independent of any client's resume.</summary>
    public const string ConsumerGroup = "realtime-hub";

    private const int BatchSize = 256;
    private static readonly TimeSpan _idlePoll = TimeSpan.FromSeconds(1);

    private readonly IServiceProvider _services;
    private readonly IHubContext<RealtimeHub> _hub;
    private readonly ILogger<RealtimeEventLogFanoutHost> _logger;

    /// <summary>Creates the fan-out over the root provider (for a scoped event-log read per pass) and the hub
    /// context it broadcasts through.</summary>
    public RealtimeEventLogFanoutHost(
        IServiceProvider services, IHubContext<RealtimeHub> hub, ILogger<RealtimeEventLogFanoutHost> logger)
    {
        _services = services;
        _hub = hub;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long? cursor = null;

        // The catch-up phase machine (PlanPass). A first-ever start (no committed cursor) swallows the whole-log
        // backlog SILENTLY — it is history, not live. A restart (committed cursor) instead brackets its outage
        // backlog with a Started / Completed pair and replays the missed events as history in between. Only
        // genuinely-live events, once caught up, broadcast unbracketed.
        RealtimeFanoutPhase phase = RealtimeFanoutPhase.SilentCatchUp;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool caughtUp;

                // A fresh scope per pass: IEventLog is scoped (it holds a scoped DbContext), so resolving it once
                // for the lifetime of the host would capture a disposed context. Same rule every hosted consumer follows.
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
                    if (cursor is null)
                    {
                        long? committed = await log.GetCursorAsync(ConsumerGroup, stoppingToken);
                        cursor = committed ?? 0;
                        // A committed cursor means a restart: decide bracket-vs-silent-vs-live once the first read
                        // reveals whether an outage backlog actually exists.
                        phase = committed is null ? RealtimeFanoutPhase.SilentCatchUp : RealtimeFanoutPhase.RestartPending;
                    }

                    EventPage page = await log.ReadAfterAsync(cursor.Value, BatchSize, stoppingToken);

                    // The hub is presentation-only, so a retention gap is not recoverable state to rebuild — a
                    // connected client simply missed some ticks. Log it and carry on from what the log returned.
                    if (page.Gap is not null)
                    {
                        _logger.LogInformation(
                            "Realtime fan-out cursor {Cursor} fell behind the retention window (oldest {Oldest}); resuming from the returned tail.",
                            page.Gap.RequestedAfterSequence, page.Gap.OldestAvailableSequence);
                    }

                    FanoutPassPlan plan = PlanPass(phase, page.Events.Count, BatchSize);

                    // Announce the catch-up BEFORE replaying any of it, so a client renders the events that follow as
                    // history. The from-cursor is the resume point — the last live sequence before the outage.
                    if (plan.AnnounceCatchUpStart)
                    {
                        await _hub.Clients.All.SendAsync(
                            RealtimeCatchUp.ClientMethod,
                            new RealtimeCatchUp(RealtimeCatchUpPhase.Started, cursor.Value),
                            stoppingToken);
                    }

                    caughtUp = page.Events.Count == 0;
                    if (!caughtUp)
                    {
                        foreach (EventEnvelope evt in page.Events)
                        {
                            if (plan.BroadcastEvents && RealtimeEventCatalog.IsBroadcast(evt.Type))
                            {
                                await _hub.Clients.All.SendAsync(
                                    RealtimeEvent.ClientMethod, RealtimeEvent.From(evt), stoppingToken);
                            }

                            cursor = evt.Sequence;
                        }

                        await log.CommitCursorAsync(ConsumerGroup, cursor.Value, stoppingToken);
                    }

                    // Announce caught-up AFTER the last historical event, once head is reached — the stream is live
                    // from here. The through-cursor is the head the catch-up reached.
                    if (plan.AnnounceCaughtUp)
                    {
                        await _hub.Clients.All.SendAsync(
                            RealtimeCatchUp.ClientMethod,
                            new RealtimeCatchUp(RealtimeCatchUpPhase.Completed, cursor.Value),
                            stoppingToken);
                    }

                    phase = plan.NextPhase;
                }

                if (caughtUp)
                {
                    await Task.Delay(_idlePoll, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // a clean stop, not a fault
            }
            catch (ObjectDisposedException)
            {
                // The root provider was torn down (host shutdown / a test factory disposing between classes) before
                // the stop token fired, so the scoped resolve threw. Exit cleanly rather than cascade the failure.
                break;
            }
            catch (Exception error)
            {
                // Transient (a dropped DB connection, a serialization hiccup): the cursor is uncommitted, so nothing
                // is skipped. Back off and retry. A fault mid-catch-up re-announces from the uncommitted cursor — an
                // at-least-once repeat the client dedupes, never a skipped or silently-live event.
                _logger.LogWarning(error, "Realtime fan-out pass failed; retrying after {Delay}.", _idlePoll);
                await Task.Delay(_idlePoll, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Decides one read pass of the catch-up phase machine (gh#645, #690): whether to announce a catch-up start,
    /// whether to broadcast this page's events, whether to announce caught-up, and the next phase. Pure, so the
    /// safety-relevant transitions — a first start stays silent, a restart brackets its outage backlog as history,
    /// and only genuinely-live events broadcast unbracketed — are pinned by unit tests rather than the hosted loop.
    /// </summary>
    /// <param name="phase">The current phase.</param>
    /// <param name="eventCount">The number of events the read returned this pass.</param>
    /// <param name="batchSize">The page size; a short page (fewer than this) means the log is drained to head.</param>
    /// <returns>The plan for this pass.</returns>
    internal static FanoutPassPlan PlanPass(RealtimeFanoutPhase phase, int eventCount, int batchSize)
    {
        bool reachedHead = eventCount < batchSize;
        return phase switch
        {
            // A cold first start: the whole log is history. Swallow it silently, never bracket, go live at head.
            RealtimeFanoutPhase.SilentCatchUp => new FanoutPassPlan(
                AnnounceCatchUpStart: false, BroadcastEvents: false, AnnounceCaughtUp: false,
                NextPhase: reachedHead ? RealtimeFanoutPhase.Live : RealtimeFanoutPhase.SilentCatchUp),

            // A restart with nothing to catch up: go live silently, no outage to announce.
            RealtimeFanoutPhase.RestartPending when eventCount == 0 => new FanoutPassPlan(
                AnnounceCatchUpStart: false, BroadcastEvents: false, AnnounceCaughtUp: false,
                NextPhase: RealtimeFanoutPhase.Live),

            // A restart with a backlog: announce the start, replay it as history, and announce caught-up once head
            // is reached (which may be this same first page).
            RealtimeFanoutPhase.RestartPending => new FanoutPassPlan(
                AnnounceCatchUpStart: true, BroadcastEvents: true, AnnounceCaughtUp: reachedHead,
                NextPhase: reachedHead ? RealtimeFanoutPhase.Live : RealtimeFanoutPhase.AnnouncedCatchUp),

            // Mid-catch-up: keep replaying the outage backlog as history; announce caught-up at head.
            RealtimeFanoutPhase.AnnouncedCatchUp => new FanoutPassPlan(
                AnnounceCatchUpStart: false, BroadcastEvents: true, AnnounceCaughtUp: reachedHead,
                NextPhase: reachedHead ? RealtimeFanoutPhase.Live : RealtimeFanoutPhase.AnnouncedCatchUp),

            // Live: broadcast every pass, stay live.
            RealtimeFanoutPhase.Live => new FanoutPassPlan(
                AnnounceCatchUpStart: false, BroadcastEvents: true, AnnounceCaughtUp: false,
                NextPhase: RealtimeFanoutPhase.Live),

            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
        };
    }
}

/// <summary>The fan-out's catch-up phase (gh#645, #690).</summary>
internal enum RealtimeFanoutPhase
{
    /// <summary>A cold first start swallowing the whole-log backlog as history — no broadcast, no bracket.</summary>
    SilentCatchUp,

    /// <summary>A restart, before the first read reveals whether an outage backlog exists to bracket.</summary>
    RestartPending,

    /// <summary>A restart's outage backlog is being replayed as history, opened by a Started bracket.</summary>
    AnnouncedCatchUp,

    /// <summary>Caught up to head — events broadcast live, unbracketed.</summary>
    Live,
}

/// <summary>One read pass's plan from the catch-up phase machine (gh#645, #690).</summary>
/// <param name="AnnounceCatchUpStart">Broadcast a <see cref="RealtimeCatchUpPhase.Started"/> bracket before this page's events.</param>
/// <param name="BroadcastEvents">Broadcast this page's allow-listed events — as history while catching up, live once caught up.</param>
/// <param name="AnnounceCaughtUp">Broadcast a <see cref="RealtimeCatchUpPhase.Completed"/> bracket after this page's events — head reached.</param>
/// <param name="NextPhase">The phase the next pass runs in.</param>
internal readonly record struct FanoutPassPlan(
    bool AnnounceCatchUpStart, bool BroadcastEvents, bool AnnounceCaughtUp, RealtimeFanoutPhase NextPhase);
