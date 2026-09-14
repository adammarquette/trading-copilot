/**
 * The validity window, turned into something an operator can act on.
 *
 * R-4's window is `min(configured window, time to this market's auto-flatten deadline)` — a suggestion can never
 * outlive the flatten that is about to close the position it would open. So **expiry is the common outcome, not
 * the exception**, and the surface has to make it obvious *before* it happens rather than reporting it after.
 * That is why this is a countdown and not a timestamp: `expires 14:32:10` asks the operator to subtract, and they
 * are subtracting from a clock they cannot see while price is moving.
 */

/**
 * How close to the edge the window is. Deliberately a small, closed vocabulary rather than a percentage: the card
 * maps these onto `palette.trading` (warn, then critical), and a continuous ramp would put an un-named colour on
 * the screen that no token owns.
 */
export type ValidityUrgency = 'normal' | 'warn' | 'critical' | 'expired';

/** Under two minutes the operator should be deciding, not reading. */
export const WARN_THRESHOLD_MS = 120_000;

/** Under thirty seconds the take is about to be refused by the server's own re-check. */
export const CRITICAL_THRESHOLD_MS = 30_000;

export interface Validity {
  /** Milliseconds left, clamped at zero — never negative, so callers cannot render a countdown running backwards. */
  readonly remainingMs: number;
  readonly urgency: ValidityUrgency;
  /** `expired` is a distinct field rather than an `urgency` comparison, because it gates actions, not colour. */
  readonly expired: boolean;
}

/**
 * Resolves a suggestion's window against a clock reading.
 *
 * The clock is a parameter rather than a `Date.now()` call so this is pure: the countdown's whole point is that it
 * changes over time, and a function that reads the clock itself can only be tested by waiting.
 */
export function validityAt(expiresAtMs: number, nowMs: number): Validity {
  const remainingMs = Math.max(0, expiresAtMs - nowMs);

  if (remainingMs <= 0) {
    return { remainingMs: 0, urgency: 'expired', expired: true };
  }
  if (remainingMs <= CRITICAL_THRESHOLD_MS) {
    return { remainingMs, urgency: 'critical', expired: false };
  }
  if (remainingMs <= WARN_THRESHOLD_MS) {
    return { remainingMs, urgency: 'warn', expired: false };
  }
  return { remainingMs, urgency: 'normal', expired: false };
}

/**
 * `m:ss`, growing to `h:mm:ss` past an hour — the shape a stopwatch uses, because that is what this is.
 *
 * Rounded **up**, not down: a window with 800ms left has not expired, and floor would show `0:00` beside a card
 * that is still takeable. The only thing that may read `0:00` is a window that has actually closed.
 */
export function formatRemaining(remainingMs: number): string {
  const totalSeconds = Math.ceil(remainingMs / 1000);
  const seconds = totalSeconds % 60;
  const minutes = Math.floor(totalSeconds / 60) % 60;
  const hours = Math.floor(totalSeconds / 3600);

  const paddedSeconds = String(seconds).padStart(2, '0');
  if (hours === 0) {
    return `${String(minutes)}:${paddedSeconds}`;
  }
  return `${String(hours)}:${String(minutes).padStart(2, '0')}:${paddedSeconds}`;
}
