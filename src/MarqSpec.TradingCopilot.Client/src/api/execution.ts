import { type ApiResult, request } from './client';

/**
 * The operator's venue-truth resting orders and net position on an account, scoped to one instrument (gh#772) — the
 * REST reads the chart's execution overlay draws (gh#727 increment 3). Owner-scoped (R-20). The `markBasis` qualifies
 * how far to trust the read (R-19, ADR-0013): `Unknown` means the venue could not be reached — declared unknown,
 * never surfaced as an empty book / flat account. Prices arrive as JSON numbers (the server's `decimal`s) — fine to
 * *draw*, never to size an order against.
 */

/** One order resting at the venue (gh#381): the resting price(s), size, and whether the leg is protective. */
export interface RestingOrder {
  readonly venueOrderKey: string;
  readonly contract: string;
  readonly stopPrice: number | null;
  readonly limitPrice: number | null;
  readonly size: number;
  readonly isProtective: boolean;
}

/** The account's resting orders on the instrument, and how far the read can be trusted (gh#381). */
export interface RestingOrders {
  readonly markBasis: string;
  readonly orders: readonly RestingOrder[];
}

/** One reconciled position from venue truth (gh#193): the signed net quantity and its average entry. */
export interface ReconciledPosition {
  readonly contract: string;
  readonly netQuantity: number;
  readonly averagePrice: number;
  readonly isFlat: boolean;
}

/** The account's positions on the instrument, and the basis their marks rest on (gh#193). */
export interface ReconciledPositions {
  readonly markBasis: string;
  readonly positions: readonly ReconciledPosition[];
}

/**
 * Reads the account's resting working orders from venue truth, scoped to one `instrument` (gh#772 / gh#381), through
 * the authenticated client (R-18 — never a raw `fetch`). The degraded outcomes stay distinct (R-19): an unreachable
 * venue is a *success* with `markBasis: 'Unknown'` and no orders (declared-unknown, not an empty book); a well-formed
 * instrument the venue has no contract for is a **refusal** (400); an account not owned by the caller a 404 **failure**
 * (R-20). `instrument` binds as the `?instrument=` query param the server scopes on.
 */
export function getWorkingOrders(
  accountId: string,
  instrument: string,
): Promise<ApiResult<RestingOrders>> {
  const query = new URLSearchParams({ instrument });
  return request<RestingOrders>('GET', `/accounts/${accountId}/orders?${query.toString()}`);
}

/**
 * Reads the account's net position from venue truth, scoped to one `instrument` (gh#772 / gh#193), through the
 * authenticated client (R-18). The same distinct outcomes as {@link getWorkingOrders}: `Unknown` for an unreachable
 * venue, a 400 refusal for an unresolvable instrument, a 404 failure for an unowned account.
 */
export function getPositions(
  accountId: string,
  instrument: string,
): Promise<ApiResult<ReconciledPositions>> {
  const query = new URLSearchParams({ instrument });
  return request<ReconciledPositions>(
    'GET',
    `/accounts/${accountId}/positions?${query.toString()}`,
  );
}
