import { type ApiResult, requestJson } from './client';

/**
 * The auto-flatten schedule behind the safety strip's countdown (gh#657, R-13).
 *
 * Auto-flatten is the one autonomous action in the system, and it only ever reduces exposure. This module reads
 * when it will next act; nothing here arms, disarms or reschedules it — that is deployed configuration.
 *
 * **Why this is a server read at all.** The deadline is a wall-clock *Central* time. A browser resolving it
 * against its own zone is an hour out for the weeks either side of a daylight-saving change, and wrong in the
 * direction that displays a deadline the scheduler will not act on. The server resolves it against the same
 * `MarketClock` the flatten itself uses, and hands back the resulting instant.
 */

/** One market's next auto-flatten deadline. */
export interface FlattenMarketDeadline {
  readonly instrument: string;
  /** The deadline in market wall-clock (Central) time, `HH:mm` — what the rulebook says. */
  readonly deadline: string;
  /** The next occurrence of that deadline as an instant, at or after {@link FlattenSchedule.asOf}. */
  readonly deadlineUtc: string;
  /** Whether auto-flatten is armed here. `false` is R-13's deliberate, warned override — shown, never hidden. */
  readonly enabled: boolean;
  /** Whether the deadline came from configuration or the built-in default. */
  readonly source: string;
}

/** The schedule as the server sees it, stamped with the server's own instant. */
export interface FlattenSchedule {
  /**
   * The server's `now` at the moment it read the schedule. Countdowns measure from **this**, not from the
   * browser clock: a workstation running fast would otherwise display safety margin that does not exist.
   */
  readonly asOf: string;
  /** One entry per governed market, soonest deadline first. */
  readonly markets: readonly FlattenMarketDeadline[];
}

/** The next auto-flatten deadline per governed market. A failure stays a failure — see {@link soonestArmed}. */
export function getFlattenSchedule(): Promise<ApiResult<FlattenSchedule>> {
  return requestJson<FlattenSchedule>('GET', '/flatten/schedule');
}

/**
 * The market the strip counts down to: the nearest deadline **that is actually armed**.
 *
 * Disabled markets are deliberately excluded rather than merely sorted last. Counting down to a market whose
 * auto-flatten is switched off would display protection that is not running, which is the most misleading thing
 * this strip could do. `null` means there is nothing to count down to, and the strip must say so explicitly.
 */
export function soonestArmed(schedule: FlattenSchedule): FlattenMarketDeadline | null {
  let soonest: FlattenMarketDeadline | null = null;

  for (const market of schedule.markets) {
    if (!market.enabled) {
      continue;
    }
    if (soonest === null || Date.parse(market.deadlineUtc) < Date.parse(soonest.deadlineUtc)) {
      soonest = market;
    }
  }

  return soonest;
}

/**
 * Milliseconds left until `market`'s deadline, given the server's `asOf` and how long the client has held that
 * reading.
 *
 * `elapsedMs` is a **duration** measured client-side, which is skew-free; the absolute instants both come from
 * the server. Re-reading the browser's wall clock instead would reintroduce exactly the skew `asOf` removes.
 * Clamped at zero: past the deadline the honest display is 00:00 and a flatten under way, not a negative number
 * that reads like time remaining.
 */
export function remainingMs(
  market: FlattenMarketDeadline,
  asOf: string,
  elapsedMs: number,
): number {
  const total = Date.parse(market.deadlineUtc) - Date.parse(asOf) - elapsedMs;

  return total > 0 ? total : 0;
}

/**
 * The broadcast event types that mean the countdown should re-read the schedule. The *arming* is deployed config,
 * static within a session — so what the strip reacts to at runtime is a **deadline PASSING**, after which the
 * soonest-armed rolls to the next session (otherwise the display sits at 00:00 until the ~5-minute refresh). Only
 * the `AutoFlattenService` outcomes that mean a governed deadline has passed belong: `executed` (flattened),
 * `missed`, and `escalated`.
 *
 * Deliberately EXCLUDED, and each for a reason (the exact-set reasoning):
 * - `flatten.warning` fires *before* the deadline — nothing has rolled, the local tick already shows the right
 *   number, and because a failed read renders "unavailable", a transient failure of a warning-driven re-read would
 *   blank the ticking countdown in the final minutes, exactly where the pre-realtime code kept ticking through it.
 * - `flatten.disabled` concerns a market the countdown already excludes (`soonestArmed` skips disabled markets).
 * - `flatten.watchdog.*` (and `unconfigured` / `unrostered`) are health / not-governed signals, not the schedule.
 */
const FLATTEN_EVENT_TYPES: ReadonlySet<string> = new Set([
  'flatten.executed',
  'flatten.missed',
  'flatten.escalated',
]);

/**
 * Whether a broadcast event should trigger an auto-flatten schedule re-read. An exact-match set, not a `startsWith`
 * prefix — the strip's other signals (and the watchdog's own events) share this channel (the {@link isProtectionEvent}
 * reasoning).
 */
export function isFlattenEvent(type: string): boolean {
  return FLATTEN_EVENT_TYPES.has(type);
}
