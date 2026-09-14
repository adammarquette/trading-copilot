using MarqSpec.TradingCopilot.Domain.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.Api.Realtime;

/// <summary>
/// The BFF realtime hub (gh#645, R-10 / R-18). Authenticated, and <b>presentation-only</b>: it has no invocable
/// method, so it can never be a second path to the broker — every state change still goes through the gated REST
/// endpoints and ADR-0007's single send path (<c>OrderExecutionService</c>). It only pushes: live events arrive on
/// every connection from <see cref="RealtimeEventLogFanoutHost"/>, and this class adds the one connect-time
/// behaviour — an <b>idempotent resume</b>.
/// </summary>
/// <remarks>
/// <b>Resume contract (ADR-0001, at-least-once).</b> A reconnecting client names the last sequence it applied via
/// the <c>?after=</c> query parameter; on connect the hub replays the durable events after it, in order, to that
/// caller alone, then the live fan-out takes over. Delivery overlaps at the boundary are deliberate — the client
/// dedupes by <see cref="RealtimeEvent.Sequence"/> (monotonic), so replay-then-live is gap-free without being
/// exactly-once. A cursor that has fallen off the 24h retention window is reported as a <see cref="RealtimeGap"/>
/// (re-fetch state over REST), never silently skipped; a resume further back than <see cref="MaxResumeEvents"/> is
/// bounded the same way rather than dumping the whole window.
/// </remarks>
public sealed class RealtimeHub : Hub
{
    /// <summary>The hub's literal route — shared by the <c>MapHub</c> call and the JWT query-string carve-out so
    /// they can never drift apart.</summary>
    public const string Path = "/hubs/realtime";

    private const int PageSize = 256;

    /// <summary>The most events a single resume replays before telling the client to re-fetch instead — the
    /// bounded-window discipline (a resume from far behind must not stream the whole retention window at connect).</summary>
    internal const int MaxResumeEvents = 5000;

    private readonly IEventLog _eventLog;
    private readonly ILogger<RealtimeHub> _logger;

    /// <summary>Creates the hub over the event log it replays a resuming client's backlog from.</summary>
    public RealtimeHub(IEventLog eventLog, ILogger<RealtimeHub> logger)
    {
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        string? raw = Context.GetHttpContext()?.Request.Query["after"];
        if (TryParseResumeAfter(raw, out long afterSequence))
        {
            try
            {
                await ReplayBacklogAsync(afterSequence, Clients.Caller, Context.ConnectionAborted);
            }
            catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
            {
                throw; // the client went away mid-replay; let SignalR tear the connection down
            }
            catch (Exception error)
            {
                // A transient read fault at connect must not drop the connection (the fan-out survives the identical
                // fault): tell the client its backlog could not be replayed so it re-fetches state over REST, then
                // let it proceed to the live stream rather than silently missing the gap.
                _logger.LogWarning(error, "Realtime resume replay from {After} failed; asked the client to re-fetch.", afterSequence);
                await Clients.Caller.SendAsync(
                    RealtimeGap.ClientMethod,
                    new RealtimeGap(afterSequence, afterSequence, DateTimeOffset.UtcNow),
                    Context.ConnectionAborted);
            }
        }

        await base.OnConnectedAsync();
    }

    /// <summary>Parses the <c>?after=</c> resume cursor. Absent, unparseable or negative means a fresh connection
    /// that wants live events only (no backlog). Pure, so it is unit-tested without a live connection.</summary>
    internal static bool TryParseResumeAfter(string? raw, out long afterSequence)
    {
        if (long.TryParse(raw, out afterSequence) && afterSequence >= 0)
        {
            return true;
        }

        afterSequence = 0; // a rejected cursor resumes nothing; never leak a negative out to the caller
        return false;
    }

    /// <summary>Replays the durable events after <paramref name="afterSequence"/> to a single resuming caller,
    /// applying the same broadcast allow-list and retention/size bounds a live connection sees.</summary>
    internal async Task ReplayBacklogAsync(
        long afterSequence, IClientProxy caller, CancellationToken cancellationToken, int maxEvents = MaxResumeEvents)
    {
        long cursor = afterSequence;
        int examined = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            EventPage page = await _eventLog.ReadAfterAsync(cursor, PageSize, cancellationToken);

            // A gap means the named cursor fell off the retention window: tell the client once (it re-fetches
            // state), then keep replaying the tail the log did return so the live stream still resumes cleanly.
            if (page.Gap is not null)
            {
                await caller.SendAsync(RealtimeGap.ClientMethod, RealtimeGap.From(page.Gap), cancellationToken);
            }

            foreach (EventEnvelope evt in page.Events)
            {
                // Bound the WORK, not just the sends: a resume from far back must not scan the whole window even
                // when most rows are non-broadcast. Report the stop point as a gap so the client re-fetches rather
                // than resuming mid-backlog, then stop replaying (live events still flow).
                if (examined >= maxEvents)
                {
                    await caller.SendAsync(
                        RealtimeGap.ClientMethod,
                        new RealtimeGap(afterSequence, cursor, evt.OccurredAt),
                        cancellationToken);
                    _logger.LogInformation(
                        "Realtime resume from {After} exceeded {Cap} events examined; asked the client to re-fetch.",
                        afterSequence, maxEvents);
                    return;
                }

                examined++;

                // Replay only the presentation signals the hub broadcasts live, so a resume shows exactly what a
                // live connection would — the same explicit boundary the fan-out applies (RealtimeEventCatalog).
                if (RealtimeEventCatalog.IsBroadcast(evt.Type))
                {
                    await caller.SendAsync(RealtimeEvent.ClientMethod, RealtimeEvent.From(evt), cancellationToken);
                }

                cursor = evt.Sequence; // advance past every row read, broadcast or not, so we never re-read it
            }

            if (page.Events.Count < PageSize)
            {
                return; // caught up to head
            }
        }
    }
}
