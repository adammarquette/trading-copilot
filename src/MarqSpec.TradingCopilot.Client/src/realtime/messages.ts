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

/**
 * A suggestion's new lifecycle state, pushed to the owning operator (gh#684, R-4). Owner-scoped like order / fill —
 * delivered only to this operator's connections, so it carries no `sequence` and is not part of the resume replay. The
 * wire is a signal to reconcile by id; the REST read model is the source of truth (ADR-0021).
 */
export interface RealtimeSuggestion {
  readonly suggestionId: string;
  readonly state: string;
  readonly at: string;
}

/**
 * A newly appended chat message, pushed to the owning operator (gh#906, R-6). Owner-scoped like order / fill /
 * suggestion — no `sequence`, not part of the resume replay. Mirrors the server's `RealtimeChatMessage`: presentation
 * -only (the message already committed and is also returned on the REST turn response), so a surface reconciles by
 * `messageId` rather than treating this as the write's source of truth. `content` is untrusted display data — never
 * re-injected as an instruction.
 */
export interface RealtimeChatMessage {
  readonly conversationId: string;
  readonly messageId: string;
  readonly sequence: number;
  /** Numeric `ChatRole` (server has no string-enum converter): User = 1, Assistant = 2, System = 3. */
  readonly role: number;
  readonly content: string;
  readonly at: string;
}

/**
 * One streamed token delta of an in-flight assistant turn (gh#906 inc 3b). Presentation-only and best-effort — a
 * dropped chunk is never fatal, since the REST turn response and the final {@link RealtimeChatMessage} are the
 * source of truth. `delta` is untrusted display data, same as the final message content.
 */
export interface RealtimeChatChunk {
  readonly conversationId: string;
  readonly delta: string;
}

/**
 * The terminator of a chat turn that **faulted** (gh#1107) — a refused / truncated / provider-faulted turn, which
 * streams its first round and then produces no assistant message at all. Without it, every connection other than
 * the one that sent the turn would keep its half-written {@link RealtimeChatChunk} draft standing forever, with no
 * error and nothing that would ever retire it. Presentation-only and best-effort like the other chat pushes.
 *
 * There is deliberately **no turn id**: the server refuses a second in-flight turn on a conversation (gh#1106), so
 * the conversation is a sufficient correlation key. `reason` is untrusted display data, or `null` when the turn
 * carried none.
 */
export interface RealtimeChatTurnFaulted {
  readonly conversationId: string;
  readonly reason: string | null;
}

/** The SignalR client-method names the hub invokes (the `SendAsync` strings). */
export const RealtimeMethod = {
  Event: 'realtimeEvent',
  Gap: 'realtimeGap',
  CatchUp: 'realtimeCatchUp',
  OrderState: 'realtimeOrderState',
  Fill: 'realtimeFill',
  Suggestion: 'realtimeSuggestion',
  ChatMessage: 'realtimeChatMessage',
  ChatChunk: 'realtimeChatChunk',
  ChatTurnFaulted: 'realtimeChatTurnFaulted',
} as const;
