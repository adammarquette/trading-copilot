namespace MarqSpec.TradingCopilot.Domain.Suggestions;

/// <summary>
/// The regime the suggestion-throttle decided (R-4 / R-5, gh#588). As daily-drawdown headroom depletes the engine
/// throttles issuance and, at the governor or the daily-target stand-down, suppresses it.
/// </summary>
public enum SuggestionThrottleMode
{
    /// <summary>Headroom is healthy — propose normally.</summary>
    Full,

    /// <summary>Headroom is depleting — fewer, higher-conviction only (a per-window cap and a conviction floor).</summary>
    Throttled,

    /// <summary>No new entries — the governor is reached, or the daily target is hit and stand-down is on.</summary>
    Suppressed,
}

/// <summary>Why the co-pilot went quiet — a distinct reason so a caller can tell the operator (gh#588, #551 wires it).</summary>
public enum SuggestionThrottleReason
{
    /// <summary>Not suppressed — <see cref="SuggestionThrottleMode.Full"/> or <see cref="SuggestionThrottleMode.Throttled"/>.</summary>
    None,

    /// <summary>The daily-drawdown governor is reached (headroom exhausted) — manage / flatten only.</summary>
    GovernorReached,

    /// <summary>The daily profit target is hit and stand-down is on — the R-5 consistency discipline.</summary>
    DailyTargetStandDown,
}

/// <summary>
/// The day-state the throttle decides over (gh#588) — all <b>values</b>, no dependencies, exactly as the
/// <see cref="Ai.AiSpendGovernor"/> is handed its spend-in-window. That is what lets the policy be written and
/// tested ahead of the persisted headroom read that will feed it (#587 / #551).
/// </summary>
/// <param name="HeadroomFraction">
/// Daily-drawdown headroom remaining, as a fraction of the day's governor budget: <c>1</c> untouched, <c>0</c> at the
/// governor, <c>&lt;= 0</c> past it. May exceed <c>1</c> on a green day (more than a full day's drawdown available).
/// </param>
/// <param name="DailyTargetReached">Whether the operator's daily profit target has been hit (R-5).</param>
public readonly record struct SuggestionThrottleContext(decimal HeadroomFraction, bool DailyTargetReached);

/// <summary>
/// The tunable policy the throttle applies (gh#588) — supplied by the caller from the operator's risk profile and
/// configuration (#551), never read here, the <see cref="Ai.AiSpendBudget"/> pattern. Constructed through
/// <see cref="Declare"/> so an out-of-range parameter throws here rather than mis-capping at issuance time.
/// </summary>
/// <param name="ThrottleThresholdFraction">
/// The headroom fraction at/above which issuance is <see cref="SuggestionThrottleMode.Full"/>; below it, throttling
/// begins. In <c>(0, 1]</c>.
/// </param>
/// <param name="FullWindowCap">The per-window issuance ceiling at Full — throttling only ever scales this <b>down</b>. Positive.</param>
/// <param name="ThrottledConvictionFloor">The minimum model confidence (0–100) a candidate needs while throttled — "higher-conviction only".</param>
/// <param name="StandDownOnDailyTarget">Whether hitting the daily profit target suppresses new entries (the R-5 stand-down; opt-in per account).</param>
public sealed record SuggestionThrottlePolicy(
    decimal ThrottleThresholdFraction,
    int FullWindowCap,
    int ThrottledConvictionFloor,
    bool StandDownOnDailyTarget)
{
    /// <summary>Declares a policy with its invariants enforced (the <see cref="Ai.AiSpendBudget.Declare"/> pattern).</summary>
    /// <param name="throttleThresholdFraction">The Full/Throttled boundary headroom fraction — in <c>(0, 1]</c>.</param>
    /// <param name="fullWindowCap">The per-window cap at Full — positive.</param>
    /// <param name="throttledConvictionFloor">The throttled conviction floor — in <c>[0, 100]</c>.</param>
    /// <param name="standDownOnDailyTarget">Whether the daily target suppresses new entries.</param>
    /// <returns>The validated policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its declared range.</exception>
    public static SuggestionThrottlePolicy Declare(
        decimal throttleThresholdFraction,
        int fullWindowCap,
        int throttledConvictionFloor,
        bool standDownOnDailyTarget)
    {
        if (throttleThresholdFraction <= 0m || throttleThresholdFraction > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throttleThresholdFraction), throttleThresholdFraction, "The throttle threshold fraction must be in (0, 1].");
        }

        if (fullWindowCap <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fullWindowCap), fullWindowCap, "The full-window issuance cap must be positive.");
        }

        if (throttledConvictionFloor is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throttledConvictionFloor), throttledConvictionFloor, "The throttled conviction floor must be in [0, 100].");
        }

        return new SuggestionThrottlePolicy(
            throttleThresholdFraction, fullWindowCap, throttledConvictionFloor, standDownOnDailyTarget);
    }
}

/// <summary>
/// What the throttle decided, and why (gh#588) — mirrors <see cref="Ai.AiSpendDecision"/>. <see cref="Explanation"/>
/// is always populated so a suppressed or throttled review's advisory can quote it to the operator.
/// </summary>
/// <param name="Mode">Full, Throttled, or Suppressed.</param>
/// <param name="PerWindowCap">
/// The most issuances this window admits — <see cref="SuggestionThrottlePolicy.FullWindowCap"/> at Full, scaled down
/// when throttled, and <c>0</c> when suppressed. Computed from headroom <b>alone</b>: a model-authored confidence can
/// never raise it.
/// </param>
/// <param name="RequiredConfidence">The minimum confidence a candidate needs (0 at Full; the floor when throttled).</param>
/// <param name="Reason">Why issuance is suppressed — <see cref="SuggestionThrottleReason.None"/> when it is not.</param>
/// <param name="Explanation">A human-readable explanation, always populated.</param>
public sealed record SuggestionThrottleDecision(
    SuggestionThrottleMode Mode,
    int PerWindowCap,
    int RequiredConfidence,
    SuggestionThrottleReason Reason,
    string Explanation)
{
    /// <summary>Whether no new entry may be issued at all this window.</summary>
    public bool IsSuppressed => Mode == SuggestionThrottleMode.Suppressed;

    /// <summary>
    /// Whether a candidate is admitted: under the per-window cap <b>and</b> meeting the conviction floor. Confidence
    /// is a filter only — it can drop a candidate below the floor, but it can never lift the headroom-derived cap
    /// (the R-4 invariant that the deterministic arm is binding and the model cannot inflate its way past it).
    /// </summary>
    /// <param name="issuedInWindow">How many suggestions have already been issued this window (never negative).</param>
    /// <param name="confidence">The candidate's model confidence, 0–100.</param>
    /// <returns><see langword="true"/> when the candidate may be issued.</returns>
    public bool Admits(int issuedInWindow, int confidence) =>
        Mode != SuggestionThrottleMode.Suppressed
        && issuedInWindow < PerWindowCap
        && confidence >= RequiredConfidence;
}

/// <summary>
/// The suggestion-issuance throttle (R-4 / R-5, gh#588) — a <b>pure, deterministic</b> policy consulted before the
/// reviewer is woken (the wiring is #551): as daily-drawdown headroom depletes it throttles issuance, and at the
/// governor or the daily-target stand-down it suppresses it. It is advisory, <b>ahead of</b> the execution gate; it
/// never enforces risk (enforcement lives below the model) and it can only ever reduce or suppress issuance.
/// </summary>
/// <remarks>
/// Dependency-free by design, exactly like <see cref="Ai.IAiSpendGovernor"/>: the caller supplies the headroom as a
/// value, so the policy reaches no store, clock, gate, or venue and stays trivially unit-testable — which is what
/// lets it land ahead of the persisted headroom read (#587) that will feed it.
/// </remarks>
public interface ISuggestionThrottle
{
    /// <summary>Decides the issuance regime for the current day-state.</summary>
    /// <param name="policy">The tunable policy (thresholds and caps).</param>
    /// <param name="context">The day-state — headroom and whether the daily target is hit.</param>
    /// <returns>The decision, with the per-window cap, the conviction floor, and the suppress reason.</returns>
    SuggestionThrottleDecision Decide(SuggestionThrottlePolicy policy, SuggestionThrottleContext context);
}

/// <inheritdoc cref="ISuggestionThrottle" />
public sealed class SuggestionThrottle : ISuggestionThrottle
{
    /// <inheritdoc />
    public SuggestionThrottleDecision Decide(SuggestionThrottlePolicy policy, SuggestionThrottleContext context)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // 1. Governor reached: the daily-drawdown headroom is exhausted. No new entries — the mirror of the risk
        //    gate's `governorRemaining <= 0 => Block`, one layer earlier and advisory.
        if (context.HeadroomFraction <= 0m)
        {
            return new SuggestionThrottleDecision(
                SuggestionThrottleMode.Suppressed, PerWindowCap: 0, RequiredConfidence: 0,
                SuggestionThrottleReason.GovernorReached,
                "Daily-drawdown governor reached — no new entries; manage or flatten only.");
        }

        // 2. Daily profit target hit with stand-down on: the R-5 consistency discipline. Independent of headroom —
        //    the operator can hit their target with plenty of drawdown room and still choose to stand down.
        if (context.DailyTargetReached && policy.StandDownOnDailyTarget)
        {
            return new SuggestionThrottleDecision(
                SuggestionThrottleMode.Suppressed, PerWindowCap: 0, RequiredConfidence: 0,
                SuggestionThrottleReason.DailyTargetStandDown,
                "Daily profit target reached — standing down; no new entries this session.");
        }

        // 3. Full: headroom at or above the throttle band.
        if (context.HeadroomFraction >= policy.ThrottleThresholdFraction)
        {
            return new SuggestionThrottleDecision(
                SuggestionThrottleMode.Full, policy.FullWindowCap, RequiredConfidence: 0,
                SuggestionThrottleReason.None,
                FormattableString.Invariant(
                    $"Full — up to {policy.FullWindowCap} suggestion(s) this window; headroom healthy."));
        }

        // 4. Throttled: within the band. Fewer (the cap scales linearly down with headroom, floored at one so the
        //    single best setup still surfaces) and higher-conviction only. The cap is a function of HEADROOM only;
        //    confidence enters solely through Admits, never here — a model cannot inflate its number past the cap.
        decimal bandFraction = context.HeadroomFraction / policy.ThrottleThresholdFraction; // in (0, 1)
        int cap = Math.Max(
            1, (int)Math.Round(policy.FullWindowCap * bandFraction, MidpointRounding.AwayFromZero));

        return new SuggestionThrottleDecision(
            SuggestionThrottleMode.Throttled, cap, policy.ThrottledConvictionFloor,
            SuggestionThrottleReason.None,
            FormattableString.Invariant(
                $"Throttled — up to {cap} higher-conviction suggestion(s) (confidence >= {policy.ThrottledConvictionFloor}) as drawdown headroom depletes."));
    }
}
