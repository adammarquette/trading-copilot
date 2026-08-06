using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Domain.MarketData;

namespace MarqSpec.TradingCopilot.Api.Realtime;

/// <summary>
/// The explicit allow-list of event-log types the realtime hub broadcasts (gh#645). An <b>explicit</b> boundary,
/// not an inherited one: the event log is a shared / global journal (its rows are never <c>IUserOwned</c> — an
/// acknowledged R-20 global), and this hub forwards only the presentation-relevant signals on it — live market data
/// (R-22) and the operator-wide safety strip, kill switch + auto-flatten (R-13 / R-16). <b>Owner-scoped data (orders,
/// fills, suggestions) is not on the log</b> and reaches clients through the per-owner follow-up seams (gh#683 /
/// gh#684), never here. A new type is broadcast only once it is added here — a deliberate, reviewed act — so nothing
/// leaks to every connection by default.
/// </summary>
internal static class RealtimeEventCatalog
{
    private static readonly HashSet<string> _broadcastTypes = new(StringComparer.Ordinal)
    {
        // Global market data (R-22) — broadcast to every connection.
        QuoteIngestionService.QuoteEventType,
        ContextTradeIngestionService.ContextTradeEventType,
        // The always-visible safety strip (R-13 / R-16) — operator-wide, single-operator deployment (ADR-0015/0017).
        KillSwitchService.EngagedEventType,
        KillSwitchService.DisengagedEventType,
        KillSwitchService.EscalatedEventType,
        AutoFlattenService.ExecutedEventType,
        AutoFlattenService.WarningEventType,
        AutoFlattenService.MissedEventType,
        AutoFlattenService.EscalatedEventType,
        AutoFlattenService.DisabledEventType,
        AutoFlattenService.UnconfiguredEventType,
        AutoFlattenWatchdogService.SavedEventType,
        AutoFlattenWatchdogService.RejectedEventType,
        AutoFlattenWatchdogService.CriticalEventType,
    };

    /// <summary>The count of allow-listed types — exposed so a test can pin the boundary against drift.</summary>
    public static int BroadcastTypeCount => _broadcastTypes.Count;

    /// <summary>True when an event type is a presentation signal the hub forwards to connected clients.</summary>
    public static bool IsBroadcast(string type) => _broadcastTypes.Contains(type);
}
