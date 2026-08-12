/**
 * Presenting the risk gate's decision on the order ticket (gh#655, R-5 / R-11, ADR-0007).
 *
 * The rule this module exists to make visible: **the size transmitted is always the one the gate approved, never
 * the one asked for.** Sending the requested quantity after a resize would make the gate advisory, and an operator
 * who believed they sent a size they did not is the single worst outcome this surface can produce.
 *
 * **The wire is asymmetric, and that is the trap here.** `Outcome` arrives as a *name* (`"Allowed"` /
 * `"Resized"` / `"Blocked"`) because the server builds it with `.ToString()`, while `BindingLayer` arrives as its
 * *integer* — there is no `JsonStringEnumConverter` server-side. So a layer must be mapped explicitly; printing
 * the field gives the operator `4` rather than "Per-trade risk".
 */

/**
 * The risk layers, **by explicit value**. Never map these positionally: gh#655's own list names only seven, and
 * the two it omits are not at the end — `DailyProfitTarget` sits at 3, in the middle. A positional mapping of the
 * card's seven would label `PerTradeRisk` as `DailyProfitTarget` and shift every layer after it, putting a
 * plausible-looking *wrong* reason on a refusal.
 */
export const RiskLayer = {
  DrawdownFloor: 0,
  DailyLossLimit: 1,
  DailyGovernor: 2,
  DailyProfitTarget: 3,
  PerTradeRisk: 4,
  MaxDrawdownPerTrade: 5,
  ManualCap: 6,
  SanityCap: 7,
  ConsistencyTarget: 8,
} as const;
export type RiskLayer = (typeof RiskLayer)[keyof typeof RiskLayer];

const LAYER_NAMES: Readonly<Record<number, string>> = {
  [RiskLayer.DrawdownFloor]: 'Drawdown floor',
  [RiskLayer.DailyLossLimit]: 'Daily loss limit',
  [RiskLayer.DailyGovernor]: 'Daily governor',
  [RiskLayer.DailyProfitTarget]: 'Daily profit target',
  [RiskLayer.PerTradeRisk]: 'Per-trade risk',
  [RiskLayer.MaxDrawdownPerTrade]: 'Max drawdown per trade',
  [RiskLayer.ManualCap]: 'Manual cap',
  [RiskLayer.SanityCap]: 'Sanity cap',
  [RiskLayer.ConsistencyTarget]: 'Consistency target',
};

/**
 * The operator-facing name of a binding layer. "Blocked" alone is not an answer; "blocked by Daily governor" is.
 *
 * An unrecognised value still names itself rather than rendering empty — if the server grows a tenth layer before
 * this client does, the operator gets something specific enough to act on instead of a refusal with no reason.
 */
export function riskLayerName(layer: number): string {
  return LAYER_NAMES[layer] ?? `Risk layer ${layer}`;
}

/** The part of a gate decision this module reasons about. */
export interface GateDecisionView {
  readonly outcome: string;
  readonly approvedQuantity: number;
}

/** What the ticket must show about size before the send control. */
export interface SizingView {
  /** What the operator asked for. */
  readonly requested: number;
  /** What will actually be transmitted — always the gate's number. */
  readonly willSend: number;
  /** Whether those differ, and so must both be shown. */
  readonly resized: boolean;
  /** The gate left room for zero contracts — a first-class outcome, not an error. */
  readonly noTrade: boolean;
  /** Whether a send control may be offered at all. */
  readonly sendable: boolean;
}

/**
 * Reconciles what was asked for against what the gate approved.
 *
 * `resized` compares the **numbers** rather than trusting `outcome`. That is deliberate defence in depth: if the
 * two ever disagree with the label, the operator is still told, so a server-side labelling slip cannot transmit a
 * size they never saw.
 */
export function describeSizing(requested: number, decision: GateDecisionView): SizingView {
  const willSend = decision.approvedQuantity;
  const noTrade = willSend <= 0;

  return {
    requested,
    willSend,
    resized: willSend !== requested,
    noTrade,
    sendable: !noTrade && isGateDecision(decision.outcome),
  };
}

/**
 * The outcomes where the gate actually sized the order. Anything else is a **pre-gate refusal** — it never
 * reached sizing, so its zero quantity means "not evaluated", not "zero contracts allowed".
 */
const GATE_OUTCOMES: ReadonlySet<string> = new Set(['Allowed', 'Resized']);

/**
 * Whether an outcome came from the gate at all. `Blocked` is deliberately absent: the gate evaluated it and said
 * no, so it is never sendable either.
 *
 * **The single owner of "may this be sent".** It lives here rather than in a component because both the decision
 * panel and the ticket's send control need it, and two independently-maintained copies of a safety-relevant
 * whitelist will eventually disagree — a new pre-gate refusal added in one place and not the other would leave a
 * Send button visible over a decision the panel is rendering as a refusal (#797 review).
 */
export function isGateDecision(outcome: string): boolean {
  return GATE_OUTCOMES.has(outcome);
}
