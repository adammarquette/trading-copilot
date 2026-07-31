using MarqSpec.TradingCopilot.Domain.Ai;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// The judgment inputs handed to the agent when a trigger fires (R-4 / ADR-0008) — the market facts of the fired
/// setup, so the prompt-injection surface is near zero (all decimals, constrained enums, and a known indicator name;
/// no free text).
/// </summary>
/// <remarks>
/// <para>
/// Account, size and mode are deliberately <b>not</b> here: they are deterministic issuance facts stamped by the
/// system, never chosen by or sent to the model — enforcement-below-the-model extended to identity and sizing.
/// <see cref="ObservedValue"/> is the measured, satisfied reading that fired the trigger (never null — a fire
/// needs a value).
/// </para>
/// <para>
/// <see cref="Enrichment"/> (gh#476) is the one <b>optional</b> field: extra <b>numeric</b> market context (recent
/// bars + recent values of the fired indicator's series) the scan attaches for the <b>deep</b> tier only. It is
/// <see langword="null"/> for every triage call and for the un-enriched path, so the triage render is unchanged.
/// It preserves the near-zero-injection invariant — still all decimals and timestamps, no free text — because the
/// only free-text source (news) is deferred. It is trailing and optional on purpose: a signature that keeps every
/// existing construction site compiling.
/// </para>
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
/// <param name="Enrichment">Deep-tier numeric enrichment (gh#476), attached by the scan; <see langword="null"/> for triage and the un-enriched path.</param>
public sealed record TriggerReviewContext(
    Guid TriggerId,
    InstrumentId Instrument,
    string Indicator,
    int Period,
    int ResolutionMinutes,
    IndicatorComparison Comparison,
    decimal Threshold,
    decimal ObservedValue,
    DateTimeOffset FiredAt,
    ReviewEnrichment? Enrichment = null);

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

    /// <summary>
    /// The platform-level AI-spend budget is reached (gh#448) — the governor blocked the call <b>before</b> it was
    /// made, so no LLM invocation and no spend. Fail-closed like the rest, but the operator <b>is</b> told a setup
    /// fired that could not be reviewed because the daily AI budget is spent. Distinct from
    /// <see cref="ReviewerUnavailable"/> (a reviewer was tried and failed) — here the reviewer was deliberately not
    /// invoked to hold the budget.
    /// </summary>
    BudgetExhausted = 6,

    /// <summary>
    /// The triage tier judged the setup too hard and deferred to the deep tier, but the caller <b>did not permit the
    /// escalation</b> (gh#478) — so the <b>deep</b> call was skipped and the triage's own answer was a defer, leaving no
    /// proposal. The triage call still happened and is still billed; only the expensive deep call was withheld. A
    /// <b>neutral</b> reason on purpose: the reviewer is told a plain permission bit and knows nothing about
    /// <i>why</i> escalation was declined — the caller (which holds the budget) frames the operator-facing message.
    /// Distinct from <see cref="BudgetExhausted"/>, where no call is made at all.
    /// </summary>
    EscalationDeclined = 7,
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
    /// <param name="Rationale">
    /// The model's plain-language rationale, <b>persisted</b> on the suggestion since gh#542 (it was previously
    /// generated, billed and discarded — and the older claim that it was "surfaced + traced" was never true: nothing
    /// logged it, traced it or showed it). Length-capped and validated at the reviewer's parse boundary. It is
    /// <b>untrusted display data</b> — never re-injected into a later prompt as instruction, and in particular never
    /// carried into the deep-review enrichment (gh#476).
    /// </param>
    /// <param name="Confidence">
    /// The model's confidence, 0–100 (gh#543). <b>Display only</b>: it never influences size (the operator's
    /// trigger's), geometry validation, the risk gate, or whether the suggestion is issued — a zero-confidence
    /// proposal still becomes a row, because the operator decides and the model does not self-censor by number.
    /// </param>
    public sealed record Suggest(
        OrderSide Side,
        decimal EntryPrice,
        decimal StopPrice,
        decimal TargetPrice,
        string Rationale,
        int Confidence) : ReviewOutcome;

    /// <summary>The review produced no suggestion, for the given reason.</summary>
    /// <param name="Reason">Why.</param>
    /// <param name="Detail">A short human-readable detail (logged / traced).</param>
    public sealed record Suppress(SuppressReason Reason, string Detail) : ReviewOutcome;
}

/// <summary>
/// The result of one review (gh#431, gh#449): the <see cref="ReviewOutcome"/> the route acts on, plus the
/// <see cref="AiCallCost"/> of <b>each</b> LLM call it made — so the owner-scoped caller can ledger the spend (ADR-0008).
/// </summary>
/// <remarks>
/// <see cref="Costs"/> is <b>empty</b> when no call was made (the inert reviewer, a budget short-circuit, a reviewer
/// that threw before calling); one entry for a single triage call; and <b>two</b> when the triage tier escalated to
/// the deep tier (gh#449) — one row per billed call, in call order, <b>including a failed one</b> (zero tokens,
/// <see cref="AiUsageOutcome.Failed"/>), so no spend regime is silently uncounted. The costs carry no owner: the owner
/// is the caller's to stamp (it holds the tenancy authority, not the reviewer).
/// </remarks>
/// <param name="Outcome">What the route should do — suggest or suppress.</param>
/// <param name="Costs">The cost of each LLM call the review made, in call order; empty if none was made.</param>
public sealed record AgentReview(ReviewOutcome Outcome, IReadOnlyList<AiCallCost> Costs);

/// <summary>
/// Reviews a fired trigger and returns a proposal or a suppression (ADR-0008) — the agent-review route's judgment
/// step, invoked <b>once per fire</b>. The agent only proposes; it never places, sizes, or authorizes an order.
/// </summary>
public interface ITriggerReviewer
{
    /// <summary>
    /// A <b>conservative upper estimate</b> of a single <b>deep</b>-tier call's cost in USD (gh#478) — what one
    /// escalation would add on top of the triage spend. The caller (the scan, which holds the spend tally and the
    /// budget) reads this to decide whether an escalation is affordable, then passes the verdict as
    /// <c>allowEscalate</c> into <see cref="ReviewAsync"/>. Exposing the estimate keeps the token-shape + rate knowledge
    /// where the deep call lives while keeping the <b>budget</b> out of the reviewer (the gh#449 purity constraint): the
    /// reviewer reports what it would cost; it never learns what the budget is. Zero for a reviewer that never escalates.
    /// </summary>
    decimal EstimatedDeepCallCostUsd { get; }

    /// <summary>Reviews one fired trigger.</summary>
    /// <param name="context">The fired setup's market facts.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <param name="allowEscalate">
    /// Whether the caller permits an escalation to the <b>deep</b> tier (gh#478). A plain permission bit — the reviewer
    /// receives <b>only</b> this, never the budget it was derived from, keeping the reviewer pure of the governor
    /// (gh#449). When the triage tier defers but this is <see langword="false"/>, the deep call is skipped and the review
    /// suppresses with <see cref="SuppressReason.EscalationDeclined"/>. Trailing-optional defaulting to
    /// <see langword="true"/> (the pre-gh#478 always-escalate behaviour), mirroring gh#476's trailing-optional so every
    /// existing call site compiles unchanged; the scan always passes it explicitly.
    /// </param>
    /// <returns>The <see cref="ReviewOutcome"/> to act on, plus the cost of any LLM call made.</returns>
    Task<AgentReview> ReviewAsync(
        TriggerReviewContext context, CancellationToken cancellationToken, bool allowEscalate = true);
}
