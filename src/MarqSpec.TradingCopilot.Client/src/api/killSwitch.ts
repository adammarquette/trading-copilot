import { type ApiResult, request, requestJson } from './client';

/**
 * The kill switch (gh#189, gh#657, R-11, ADR-0007) — the operator's process-wide panic control, and the reason
 * the safety strip is always visible: a kill switch one navigation away is not a kill switch.
 *
 * Two properties are worth surfacing rather than hiding, and the surface above this module says both out loud:
 *
 * 1. **It survives a restart.** The state is rehydrated at startup, so nothing silently re-enables trading. The
 *    client therefore always loads the state from the server and never assumes a fresh session starts disengaged.
 * 2. **It stops new risk, not protection.** While engaged it refuses every transmission *before* the order is
 *    sized, yet reducing actions still work — auto-flatten's close and stop promotion do not route through the
 *    send path. An operator must not believe engaging it strands their open positions.
 */

/** What engaging does to open positions. Serialized as its **integer** on the request — see {@link engageKillSwitch}. */
export const KillSwitchMode = { FlattenAll: 0, HaltOnly: 1 } as const;
export type KillSwitchMode = (typeof KillSwitchMode)[keyof typeof KillSwitchMode];

/** The current kill-switch state. `mode` arrives as the enum **name**, not its integer. */
export interface KillSwitchState {
  readonly engaged: boolean;
  readonly mode: string;
  readonly engagedAt: string | null;
  readonly reason: string | null;
}

/** What engaging actually did — reported back so the operator sees the effect, not just the intent. */
export interface EngageKillSwitchResult {
  readonly mode: string;
  readonly cancelledOrders: number;
  readonly flattenedPositions: number;
}

/** The current state. Always read from the server: it outlives both the page and the process. */
export function getKillSwitch(): Promise<ApiResult<KillSwitchState>> {
  return requestJson<KillSwitchState>('GET', '/kill-switch');
}

/**
 * Engages the kill switch: stop new orders, cancel working orders, then flatten or halt.
 *
 * `confirmed` is the hold-to-confirm gesture R-11 requires — the server answers 422 without it, so that a panic
 * action cannot fire from a stray request. It is set here because reaching this function *is* the completed
 * gesture; callers must not invoke it on a plain click.
 *
 * The mode goes over the wire as its **integer**. There is no `JsonStringEnumConverter` server-side, so sending
 * the name binds nothing and silently falls back to `FlattenAll` — which would flatten positions the operator
 * explicitly asked to leave on their native safety stops. The response returns the name.
 */
export function engageKillSwitch(
  mode: KillSwitchMode,
  reason: string | null,
): Promise<ApiResult<EngageKillSwitchResult>> {
  return requestJson<EngageKillSwitchResult>('POST', '/kill-switch', {
    mode,
    confirmed: true,
    reason,
  });
}

/** Disengages the kill switch, re-enabling outbound orders. */
export function disengageKillSwitch(): Promise<ApiResult<void>> {
  return request<void>('POST', '/kill-switch/disengage');
}

/**
 * The broadcast event types that mean the kill-switch state changed. All three belong: `engaged` and `disengaged`
 * are the operator's own toggles (which a *second* window, a pop-out per gh#651, will not otherwise learn about),
 * and `escalated` is the watchdog's automatic escalation — each changes what the strip must show.
 *
 * Mirrors `KillSwitchService.EngagedEventType` / `DisengagedEventType` / `EscalatedEventType`.
 */
const KILL_SWITCH_EVENT_TYPES: ReadonlySet<string> = new Set([
  'killswitch.engaged',
  'killswitch.disengaged',
  'killswitch.escalated',
]);

/**
 * Whether a broadcast event should trigger a kill-switch re-read. An exact-match set, not a `startsWith` prefix:
 * the safety strip's other signals share this channel, and a prefix test would quietly adopt any future
 * `killswitch.*` type — including one that means something else (the {@link isProtectionEvent} reasoning).
 */
export function isKillSwitchEvent(type: string): boolean {
  return KILL_SWITCH_EVENT_TYPES.has(type);
}
