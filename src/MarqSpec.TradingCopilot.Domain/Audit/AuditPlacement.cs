namespace MarqSpec.TradingCopilot.Domain.Audit;

/// <summary>
/// Where the protection an <c>AuditRecord</c> concerns physically rests (data dictionary §12, ADR-0007).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (the fail-closed-zero convention, gh#60). The distinction is the
/// point of the record: <see cref="Synthetic"/> protection depends on the platform being live and so carries
/// orphan risk on a connection drop, whereas <see cref="Native"/> protection is exchange-held and survives it.
/// </remarks>
public enum AuditPlacement
{
    /// <summary>Not a placement — the refusable zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>Exchange-held — the venue owns it, and it survives a platform outage.</summary>
    Native = 1,

    /// <summary>
    /// Held synthetically by the platform — off the book, so it cannot be anticipated or stop-hunted, but it
    /// depends on the connection being live. An orphan risk on a drop; this is what <c>synthetic_risk</c> flags.
    /// </summary>
    Synthetic = 2,

    /// <summary>
    /// The record concerns <b>no single protection placement</b> — an account- or system-level safety action, not a
    /// stop plan (gh#765): a kill-switch transition or an auto-flatten run. Distinct from <see cref="Unknown"/>,
    /// which is the never-persisted refusable zero; <see cref="None"/> is a real, deliberate value that says "this
    /// event is not about where a protective leg rests".
    /// </summary>
    None = 3,
}
