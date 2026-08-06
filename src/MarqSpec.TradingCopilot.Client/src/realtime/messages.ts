// The realtime hub's client-message contract (gh#649), consuming the gh#645 hub — ADR-0021. These wire shapes
// mirror the server records in src/MarqSpec.TradingCopilot.Api/Realtime/*.cs, and the method names are the SignalR
// SendAsync strings the connection listens on. Server enums serialize as integers (the API has no string-enum
// converter), so RealtimeCatchUpPhase is a numeric union.

/**
 * A broadcast event off the event log — global market data (R-22) and the operator-wide safety strip (R-13/R-16).
 * `sequence` is monotonic: it is both the resume cursor and the dedupe key. `payload` is a raw JSON string, parsed
 * by the consuming surface according to `type`.
 */
export interface RealtimeEvent {
  readonly sequence: number;
  readonly type: string;
  readonly occurredAt: string;
  readonly payload: string;
}

/**
 * Told to a resuming client whose named cursor fell off the 24h retention window, or whose resume exceeded the
 * server's size bound. Not an error — re-fetch current state over REST, then keep consuming live.
 */
export interface RealtimeGap {
  readonly requestedAfterSequence: number;
  readonly oldestAvailableSequence: number;
  readonly oldestAvailableOccurredAt: string;
}

/** The phase of a fan-out restart catch-up bracket (gh#690). Serialized as an integer. */
export const RealtimeCatchUpPhase = { Started: 0, Completed: 1 } as const;
export type RealtimeCatchUpPhase = (typeof RealtimeCatchUpPhase)[keyof typeof RealtimeCatchUpPhase];

/**
 * Brackets a server fan-out restart's replay: every {@link RealtimeEvent} between `Started` and `Completed` is
 * **history, not live**, so a historical kill-switch / auto-flatten is never rendered as a live safety banner.
 */
export interface RealtimeCatchUp {
  readonly phase: RealtimeCatchUpPhase;
  readonly sequence: number;
}

/**
 * An order's new status, pushed to the owning operator (gh#683) — the complete status stream (fill-driven
 * PartiallyFilled / Filled and terminal Cancelled / Rejected). Owner-scoped: delivered only to this operator's
 * connections, so it carries no sequence and is not part of the resume replay.
 */
export interface RealtimeOrderState {
  readonly venueOrderKey: string;
  readonly status: string;
  readonly at: string;
}

/** A fill on the operator's account (gh#683). Owner-scoped; `venueFillKey` is the idempotency key. */
export interface RealtimeFill {
  readonly venueOrderKey: string;
  readonly venueFillKey: string;
  readonly price: number;
  readonly size: number;
  readonly at: string;
}

/** The SignalR client-method names the hub invokes (the `SendAsync` strings). */
export const RealtimeMethod = {
  Event: 'realtimeEvent',
  Gap: 'realtimeGap',
  CatchUp: 'realtimeCatchUp',
  OrderState: 'realtimeOrderState',
  Fill: 'realtimeFill',
} as const;
