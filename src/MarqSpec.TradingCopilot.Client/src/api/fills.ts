import { type ApiResult, requestJson } from './client';

/**
 * The operator's journaled fills on an account for one instrument over a window (gh#792) — the read behind the
 * gh#727 chart fill-marker overlay. Owner-scoped (R-20). A **journal** read (the durable fill history), not venue
 * truth. Prices arrive as JSON numbers (the server's `decimal`s) — fine to *draw*, never to size an order against.
 */

/** One journaled fill (gh#792): its execution price, size, side, fees and time. */
export interface Fill {
  readonly venueFillKey: string | null;
  /** The order side the fill executed — `Buy` / `Sell`. */
  readonly side: string;
  readonly price: number;
  readonly size: number;
  readonly fees: number;
  /** When the fill executed (ISO-8601 UTC). */
  readonly executedAt: string;
}

/** The account's fills on the instrument in the window (gh#792), ascending by execution time. */
export interface Fills {
  readonly fills: readonly Fill[];
}

/**
 * Reads the operator's journaled fills on `accountId` for `instrument` over `[from, to)` (gh#792), through the
 * authenticated client (R-18 — never a raw `fetch`). The degraded outcomes stay distinct (R-11 / R-19): an over-wide
 * window or a malformed instrument / window is a **refusal** (400 — a client mistake worth naming), an account not
 * owned by the caller a 404 **failure** (R-20), and an owned account with no trades in the window a success with an
 * **empty** `fills`. `from` / `to` are ISO-8601 UTC instants.
 */
export function getFills(
  accountId: string,
  instrument: string,
  from: string,
  to: string,
): Promise<ApiResult<Fills>> {
  const query = new URLSearchParams({ instrument, from, to });
  return requestJson<Fills>('GET', `/accounts/${accountId}/fills?${query.toString()}`);
}
