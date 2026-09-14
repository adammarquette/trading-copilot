import { type ApiResult, requestJson } from './client';

/**
 * The operator's synthetic-protection state (gh#222, R-11 / R-19) — the ground truth behind the safety strip's
 * degraded indicator.
 *
 * **The read is the state; the realtime events are only the prompt to take it again.** The hub replays a backlog
 * only when the client presents an `?after=` cursor, and a freshly loaded page has none — so a reload while
 * protection is degraded receives no orphan event and would otherwise render as protected. Reading rather than
 * folding the event payloads also means there is no client-side arithmetic to drift, and a *replayed* event and a
 * *live* one lead to exactly the same re-read.
 *
 * R-19 is the distinction this surface exists to preserve: degraded is **never** unprotected. The native safety
 * stop stays with the position as the physical floor; it is the operator's tighter synthetic stop that is orphaned.
 */

/** The current protection state. */
export interface ProtectionState {
  /** Whether any of the operator's hidden working stops are currently orphaned. */
  readonly degraded: boolean;
  /** How many are orphaned right now; zero whenever {@link degraded} is false. */
  readonly orphanedStops: number;
  /** Always true — returned rather than assumed, so the surface states R-19 without hard-coding a safety claim. */
  readonly nativeSafetyStopRemainsFloor: boolean;
}

/** The current state. A failure stays a failure — it must never resolve to "protected". */
export function getProtectionState(): Promise<ApiResult<ProtectionState>> {
  return requestJson<ProtectionState>('GET', '/protection');
}

/**
 * The broadcast event types that mean protection changed. Both directions are here on purpose: `restored` is what
 * clears the alert, and ignoring it would strand a resolved degradation on screen.
 *
 * Mirrors `OrphanGuardService.OrphanedEventType` / `RestoredEventType`.
 */
const PROTECTION_EVENT_TYPES: ReadonlySet<string> = new Set([
  'protection.orphaned',
  'protection.restored',
]);

/**
 * Whether a broadcast event should trigger a protection re-read. An exact-match set rather than a prefix test:
 * the strip's other signals share this channel, and a `startsWith` shortcut would quietly adopt any future
 * `protection.*` type — including one that means something else.
 */
export function isProtectionEvent(type: string): boolean {
  return PROTECTION_EVENT_TYPES.has(type);
}
