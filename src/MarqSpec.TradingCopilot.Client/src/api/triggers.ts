import { type ApiResult, request, requestJson } from './client';

/**
 * The operator's standing deterministic triggers (R-7, R-4, ADR-0008) — the rulebook's built half. gh#660 is
 * blocked on gh#19 for *playbooks*; this is the trigger surface split out of it (gh#991).
 *
 * **The state that matters here is `confirmation`, not `enabled`.** A newly authored trigger defaults to
 * {@link TriggerConfirmation.Unconfirmed} and is **inert regardless of `enabled`** — the confirm-before-live gate
 * gh#470 added and gh#486 proved. So `enabled: true` alone does not mean a trigger will ever fire, and a surface
 * that renders the two as equal-weight badges invites exactly the wrong conclusion: an operator relying on an
 * alert that is never coming. That is the silent-monitor failure ADR-0019 exists to close.
 *
 * Enum fields cross the wire as **numbers**, not strings. The API applies no `JsonStringEnumConverter`, and the
 * endpoints that appear to return enum text (`status`, `markBasis`) call `.ToString()` by hand — `TriggerResponse`
 * does not. The generated spec confirms it (`IndicatorComparison: number`), which is why these are numeric unions
 * rather than string literals.
 */

/** Whether the operator has accepted a trigger for live evaluation. Mirrors `TriggerConfirmation`. */
export const TriggerConfirmation = {
  /** Authored but not accepted — **inert regardless of `enabled`**. The default. */
  Unconfirmed: 0,
  /** Accepted for live evaluation; the scan may fire it once `enabled`. */
  Confirmed: 1,
} as const;

/** How the trigger's value is compared with its threshold. Mirrors `IndicatorComparison`. */
export const IndicatorComparison = {
  /** Not a real comparison — never satisfies (fail-closed). */
  Unknown: 0,
  /** At or below the threshold. */
  Below: 1,
  /** At or above the threshold. */
  Above: 2,
} as const;

/** Where a satisfied trigger goes. Mirrors `TriggerRoute`. */
export const TriggerRoute = {
  /** Not a real route — refused. */
  Unknown: 0,
  /** A deterministic mechanical alert; no LLM in the loop. */
  Mechanical: 1,
  /** Routed for agent review. */
  AgentReview: 2,
} as const;

/**
 * Where the trigger sits in its fire/re-arm cycle. Mirrors `TriggerArmState`. Read-only: the scan owns it.
 *
 * **Not surfaced yet.** Carried on {@link Trigger} because the read returns it, but no surface renders it — a
 * `Fired` (held) trigger and an `Armed` one look identical today. Worth showing in the authoring increment, so a
 * debounced trigger is not mistaken for a broken one.
 */
export const TriggerArmState = {
  /** Never evaluated since creation / re-enable / edit — the next scan seeds silently, without firing. */
  Unseeded: 0,
  /** Last seen unsatisfied — the next satisfied reading is a crossing edge and fires. */
  Armed: 1,
  /** Last seen satisfied and already fired — held until it clears and re-arms. */
  Fired: 2,
} as const;

/**
 * One standing trigger. The API returns this as an anonymous object, so the shape is named here rather than
 * generated (see the module note in `client.ts`).
 */
export interface Trigger {
  readonly id: string;
  /** The venue-neutral instrument symbol (e.g. `ES`). */
  readonly symbol: string;
  /** The R-22 indicator name (`atr` / `rsi`). */
  readonly indicator: string;
  readonly period: number;
  /** The bar size in minutes. */
  readonly resolutionMinutes: number;
  readonly comparison: number;
  readonly threshold: number;
  /** The re-arm dead-band, when one is set. */
  readonly hysteresis: number | null;
  readonly route: number;
  readonly accountId: string | null;
  readonly size: number | null;
  readonly severity: number;
  /** Whether the operator has switched it on — **necessary but not sufficient** for it to fire. */
  readonly enabled: boolean;
  /** Whether it is accepted for live evaluation. Unconfirmed is inert whatever `enabled` says. */
  readonly confirmation: number;
  readonly armState: number;
  readonly armCycle: number;
  readonly lastEvaluatedValue: number | null;
  readonly lastFiredAt: string | null;
  readonly createdAt: string;
}

/**
 * Whether this trigger can actually fire, as far as a client can tell. In one place deliberately: every surface
 * answering "is this live?" must answer it the same way, and the combination — enabled but unconfirmed — is the
 * one an operator is most likely to misread.
 *
 * **Not the scan's full predicate.** The server also requires a real {@link TriggerRoute}; a row with
 * `Route.Unknown` would read as live here and still never fire. That is unreachable today — the route is refused
 * at the boundary, absent from the patch DTO, and backstopped by `CK_Triggers_Route_NotUnknown` (live in the
 * current model snapshot, not just the migration that added it) — so this is recorded rather than defended
 * against. **Two things would break it**, and the second is the likelier: a route becoming patchable, or a THIRD
 * `TriggerRoute` member — a new value passes the not-Unknown check and the boundary refusal alike, while the
 * scan's whitelist still excludes it, so this would say "Live" for a trigger that never fires.
 */
export function willFire(trigger: Trigger): boolean {
  return trigger.enabled && trigger.confirmation === TriggerConfirmation.Confirmed;
}

/** The operator's triggers. Owner-scoped server-side (R-20); the client adds no filter of its own. */
export function listTriggers(): Promise<ApiResult<Trigger[]>> {
  return requestJson<Trigger[]>('GET', '/api/triggers');
}

/** One trigger by id. */
export function getTrigger(id: string): Promise<ApiResult<Trigger>> {
  return requestJson<Trigger>('GET', `/api/triggers/${id}`);
}

/**
 * Accepts a trigger for live evaluation. Returns the updated trigger so the caller renders what the server
 * decided rather than assuming the confirm took — the same read-back discipline the rest of the client uses.
 */
export function confirmTrigger(id: string): Promise<ApiResult<Trigger>> {
  return requestJson<Trigger>('POST', `/api/triggers/${id}/confirm`);
}

/** Deletes a trigger. The response carries no payload the surface reads. */
export function deleteTrigger(id: string): Promise<ApiResult<void | undefined>> {
  return request<void>('DELETE', `/api/triggers/${id}`);
}
