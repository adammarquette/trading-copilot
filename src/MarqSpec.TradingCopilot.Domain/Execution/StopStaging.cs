namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// Where a protective stop physically rests (ADR-0007, gh#11).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (the fail-closed-zero convention, gh#60): an uninitialised
/// staging state must never read as "already at the exchange".
/// </remarks>
public enum StopStaging
{
    /// <summary>Not a staging state — the refusable zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>
    /// Held <b>synthetically</b> by the platform — not on the book, so the entry cannot be anticipated or
    /// stop-hunted. Depends on the platform being live, which is why the safety stop rests natively beyond it.
    /// </summary>
    Hidden = 1,

    /// <summary>Promoted to a <b>native working order</b> at the venue — exchange-held, survives an outage.</summary>
    Native = 2,

    /// <summary>
    /// <b>Orphaned</b> by a venue-connection loss (ADR-0007, ADR-0013, gh#209): a hidden working stop the
    /// platform can no longer promote, because the market connection that feeds its proximity is down. The
    /// position is not unprotected — the <b>native safety stop</b> beyond it remains the physical floor — but the
    /// operator's <i>tighter</i> protection is degraded until reconnect, when the guard <b>re-arms</b> it to
    /// <see cref="Hidden"/>. The promotion watcher acts only on <see cref="Hidden"/>, so an orphaned stop never
    /// promotes.
    /// </summary>
    Orphaned = 3,

    /// <summary>
    /// <b>Retired</b> by re-arm re-validation (ADR-0013, gh#191): on reconnect, the position this stop protected
    /// was found — against <b>venue truth</b>, never local state — to have <b>closed</b> (or reversed) during the
    /// outage, so the plan is retired rather than re-armed. A stop for a position that no longer exists must not
    /// promote (ADR-0013's "never auto-act on rehydrated state — re-validate first"). Terminal: the promotion
    /// watcher acts only on <see cref="Hidden"/>, so a retired plan never promotes and is never re-armed.
    /// </summary>
    Retired = 4,
}
