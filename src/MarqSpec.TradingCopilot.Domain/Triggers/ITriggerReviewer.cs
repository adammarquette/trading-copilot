using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// The judgment inputs handed to the agent when a trigger fires (R-4 / ADR-0008) — <b>only</b> the market facts of
/// the fired setup, so the prompt-injection surface is near zero (all decimals, constrained enums, and a known
/// indicator name; no free text).
/// </summary>
/// <remarks>
/// Account, size and mode are deliberately <b>not</b> here: they are deterministic issuance facts stamped by the
/// system, never chosen by or sent to the model — enforcement-below-the-model extended to identity and sizing.
/// <see cref="ObservedValue"/> is the measured, satisfied reading that fired the trigger (never null — a fire
/// needs a value).
/// </remarks>
/// <param name="TriggerId">The trigger that fired.</param>
/// <param name="Instrument">The instrument the indicator was read for.</param>
/// <param name="Indicator">The R-22 indicator name (e.g. <c>rsi</c>).</param>
/// <param name="Period">The indicator period.</param>
/// <param name="ResolutionMinutes">The bar size the indicator was computed over.</param>
/// <param name="Comparison">The threshold comparison that fired.</param>
/// <param name="Threshold">The threshold.</param>
/// <param name="ObservedValue">The measured indicator value at the fire.</param>
/// <param name="FiredAt">When the fire was observed.</param>
public sealed record TriggerReviewContext(
    Guid TriggerId,
    InstrumentId Instrument,
    string Indicator,
    int Period,
    int ResolutionMinutes,
    IndicatorComparison Comparison,
    decimal Threshold,
    decimal ObservedValue,
    DateTimeOffset FiredAt);

/// <summary>Why an agent review produced no suggestion.</summary>
public enum SuppressReason
{
    /// <summary>The agent reviewed the setup and judged it not worth surfacing — a silent, legitimate outcome.</summary>
    NotWorthSurfacing = 1,

    /// <summary>No reviewer is configured yet — the operator is told (a setup fired that could not be reviewed).</summary>
    NoReviewerConfigured = 2,

    /// <summary>The model's output could not be parsed into a valid proposal — fail-closed, logged, no suggestion.</summary>
    MalformedOutput = 3,

    /// <summary>The proposed geometry was incoherent (wrong-side stop/target, non-positive price) — rejected before persist.</summary>
    InvalidGeometry = 4,

    /// <summary>
    /// The reviewer could not be reached — a provider fault (a network error, a timeout, a rate-limit or a 5xx), or a
    /// bad completion with no usable text. Fail-closed like the rest: no suggestion, but the operator <b>is</b> told a
    /// setup fired that could not be reviewed. Distinct from <see cref="MalformedOutput"/> (the model answered, badly)
    /// and <see cref="NoReviewerConfigured"/> (there is no reviewer) — here a configured reviewer was tried and failed.
    /// </summary>
    ReviewerUnavailable = 5,
}

/// <summary>
/// The closed outcome of an agent review (ADR-0008) — the model either proposes a <see cref="Suggest"/> or the
/// review <see cref="Suppress"/>es. A closed hierarchy (private constructor) forces an exhaustive switch at the
/// call site, like the gate's decision types.
/// </summary>
public abstract record ReviewOutcome
{
    private ReviewOutcome()
    {
    }

    /// <summary>
    /// The agent proposes a setup. <b>Carries no size</b> — size is the operator-authored trigger's, never the
    /// model's — and the geometry is validated + re-checked by the take-time risk gate before anything executes.
    /// </summary>
    /// <param name="Side">The proposed direction.</param>
    /// <param name="EntryPrice">The proposed entry.</param>
    /// <param name="StopPrice">The proposed stop.</param>
    /// <param name="TargetPrice">The proposed target.</param>
    /// <param name="Rationale">The model's plain-language rationale (surfaced + traced; not persisted in this increment).</param>
    public sealed record Suggest(
        OrderSide Side,
        decimal EntryPrice,
        decimal StopPrice,
        decimal TargetPrice,
        string Rationale) : ReviewOutcome;

    /// <summary>The review produced no suggestion, for the given reason.</summary>
    /// <param name="Reason">Why.</param>
    /// <param name="Detail">A short human-readable detail (logged / traced).</param>
    public sealed record Suppress(SuppressReason Reason, string Detail) : ReviewOutcome;
}

/// <summary>
/// Reviews a fired trigger and returns a proposal or a suppression (ADR-0008) — the agent-review route's judgment
/// step, invoked <b>once per fire</b>. The agent only proposes; it never places, sizes, or authorizes an order.
/// </summary>
public interface ITriggerReviewer
{
    /// <summary>Reviews one fired trigger.</summary>
    /// <param name="context">The fired setup's market facts.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A <see cref="ReviewOutcome.Suggest"/> or <see cref="ReviewOutcome.Suppress"/>.</returns>
    Task<ReviewOutcome> ReviewAsync(TriggerReviewContext context, CancellationToken cancellationToken);
}
