namespace MarqSpec.TradingCopilot.Domain.Notifications;

/// <summary>
/// Reaches the operator when they are away from the desk (gh#243, ADR-0019) — <b>Layer 1</b> of the alerting
/// model: the push the app sends itself, because it knows first.
/// </summary>
/// <remarks>
/// <para>
/// Three layers cover each other's blind spots. This one is the fastest: the app knows the instant a flatten
/// escalates, and routing that through a metrics scrape plus a rule evaluation would cost tens of seconds on a
/// path where CL and GC have roughly fifteen minutes of margin in total. Alertmanager rules (gh#245) cover what
/// the app cannot self-report, and the out-of-process dead-man's switch (gh#244) covers the app being dead.
/// </para>
/// <para>
/// <b>The seam is deliberately transport-free.</b> ADR-0015 requires integrations to sit behind an interface a
/// fork can swap, so nothing here names a priority number, a chat thread, or a webhook. Pushover is the first
/// adapter; Discord (gh#100) and web push (ADR-0010) become further ones rather than competing paths.
/// </para>
/// <para>
/// <b>Implementations must never throw or block for long.</b> This is called from the auto-flatten path, and an
/// alerting fault that breaks or delays a flatten is the self-inflicted wound ADR-0019 rules out. A failed send
/// returns <see langword="false"/>.
/// </para>
/// </remarks>
public interface INotificationChannel
{
    /// <summary>Sends a notification, at most once per incident (see <see cref="Notification.DedupKey"/>).</summary>
    /// <param name="notification">What to tell the operator.</param>
    /// <param name="cancellationToken">The caller's token; a genuine cancellation propagates.</param>
    /// <returns><see langword="true"/> if it reached the operator's channel.</returns>
    Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken);

    /// <summary>
    /// Closes an incident: cancels any outstanding page for <paramref name="dedupKey"/> and re-arms the key, so a
    /// later recurrence is reported as the new incident it is.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A <see cref="NotificationSeverity.Page"/> nags until acknowledged, so a condition that
    /// clears itself — the watchdog catching what the primary missed — must stop waking someone for something
    /// that is no longer true. And without re-arming, the <i>second</i> failure of the day would be silently
    /// suppressed as a duplicate of the first.
    /// </remarks>
    /// <param name="dedupKey">The incident key that has cleared.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task ResolveAsync(string dedupKey, CancellationToken cancellationToken);
}
