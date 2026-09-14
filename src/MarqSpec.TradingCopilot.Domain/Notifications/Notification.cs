namespace MarqSpec.TradingCopilot.Domain.Notifications;

/// <summary>
/// Something the operator should be told, and how loudly (gh#243, ADR-0019).
/// </summary>
/// <param name="Severity">How loudly it should arrive.</param>
/// <param name="Title">A short subject — what happened.</param>
/// <param name="Body">The detail: enough to decide whether to act without opening a dashboard.</param>
/// <param name="DedupKey">
/// Identifies the <b>incident</b>, not the occurrence. The auto-flatten re-emits its escalation roughly every
/// 15 s while exposure persists, so the same incident arrives over and over; everything sharing a key is one
/// incident and is reported once, until <see cref="INotificationChannel.ResolveAsync"/> closes it. Scope it to the
/// thing that can independently go wrong — an account and instrument, say — so one market's failure never
/// suppresses another's.
/// </param>
public sealed record Notification(
    NotificationSeverity Severity,
    string Title,
    string Body,
    string DedupKey);
