namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// How a venue refused an order (gh#629, safety-critical) — the discriminator a catch site needs to decide whether
/// the order might still be live. The two demand <b>opposite</b> handling, so conflating them is the hazard this
/// type exists to remove.
/// </summary>
/// <remarks>
/// <see cref="Indeterminate"/> is deliberately the <b>zero value</b> and the fail-safe default: an unrecognised or
/// unset refusal must never be read as definitive, because that is the one direction that could release a row whose
/// order is still resting at the venue. Only the adapter, at the throw site, may assert <see cref="Definitive"/> —
/// everything a catch site does not positively recognise stays indeterminate by construction.
/// </remarks>
public enum VenueRefusalKind
{
    /// <summary>
    /// The order's fate is unknown — a transport fault, a timeout, or an accepted-but-unidentified response. The
    /// order <b>may be live</b>, so the durable intent (<c>Taking</c> / <c>Firing</c>) must be kept and a human (or
    /// the reconcile against venue truth) resolves it. The safe default (R-5, ADR-0013 §9).
    /// </summary>
    Indeterminate = 0,

    /// <summary>
    /// The venue <b>definitively rejected</b> the order — it responded, in the negative, and placed nothing (the
    /// gateway's <c>!success</c> response). Nothing rests, so the row can be resolved automatically rather than left
    /// for a human. Only ever asserted by the adapter where <c>!success</c> is unambiguous.
    /// </summary>
    Definitive = 1,
}
