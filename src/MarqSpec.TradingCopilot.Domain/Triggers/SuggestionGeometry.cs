using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// A pure sanity check on a proposed suggestion's price geometry (R-4 / ADR-0008) — the second of three
/// independent layers protecting against a malformed or hostile model output.
/// </summary>
/// <remarks>
/// This is <b>sanity, not risk enforcement</b>: it rejects a self-evidently broken proposal — a non-positive
/// price, or a stop/target on the wrong side of entry — before it can be persisted as a <c>Suggestion</c>. It does
/// <b>not</b> size, check the drawdown floor or the hard caps, or compare against a live reference price — those
/// stay the take-time risk gate's job (no live price exists at fire time). The reviewer fails closed above it, and
/// the gate is the true backstop below it.
/// </remarks>
public static class SuggestionGeometry
{
    /// <summary>Validates the coherence of a proposed entry / stop / target for a side.</summary>
    /// <param name="side">The proposed direction.</param>
    /// <param name="entry">The proposed entry price.</param>
    /// <param name="stop">The proposed stop price.</param>
    /// <param name="target">The proposed target price.</param>
    /// <returns>
    /// <see langword="null"/> when the geometry is coherent; otherwise a short reason it was rejected.
    /// </returns>
    public static string? Validate(OrderSide side, decimal entry, decimal stop, decimal target)
    {
        if (entry <= 0m || stop <= 0m || target <= 0m)
        {
            return "entry, stop and target must all be positive prices";
        }

        bool ordered = side switch
        {
            // A long risks below and targets above; a short is the mirror.
            OrderSide.Buy => stop < entry && entry < target,
            OrderSide.Sell => target < entry && entry < stop,
            _ => false, // an undeclared side is never coherent (fail-closed)
        };

        return ordered
            ? null
            : $"stop and target are not on the correct sides of entry for a {side} setup";
    }
}
