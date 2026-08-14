import { type ApiResult, request } from './client';

/**
 * The live blotter's reads — positions and resting orders from **venue truth** (gh#656, R-11 / R-3, ADR-0013).
 *
 * **The trap this module exists to close.** Both server responses are `(markBasis, list)`, and both send an
 * **empty list when the basis is `Unknown`**. So an empty array means *either* genuinely flat *or* the venue
 * could not be reached — and only `markBasis` separates them. A surface that read the array alone would render
 * **flat during an outage**, which is worse than rendering a stale view: "flat" reads as *safe*.
 *
 * So the basis is resolved **before** the list exists. The unknown branch carries **no `items` field at all**,
 * which means a consumer cannot iterate it — TypeScript refuses, rather than a convention asking it not to. That
 * is the repo's preference for guards that hold by construction over guards that inspect.
 *
 * An unrecognised or missing basis resolves to `unknown`, never to `live`: ADR-0013's declared-unknown discipline
 * says uncertainty must not present as the reading that implies the view can be trusted.
 */

/** A position as the venue reports it. */
export interface BlotterPosition {
  readonly contract: string;
  /** Signed: positive long, negative short, zero flat. */
  readonly netQuantity: number;
  readonly averagePrice: number;
  readonly isFlat: boolean;
}

/** An order resting at the venue. `size` **is** carried by the read — see gh#656's stale "known gap". */
export interface BlotterRestingOrder {
  readonly venueOrderKey: string;
  /**
   * The journaled app `Order.Id` the write endpoints key on (`DELETE /orders/{id}`, `PATCH /orders/{id}/price`),
   * matched to this venue order server-side by its venue key (gh#656). `null` for a venue-spawned protective leg,
   * which carries no `Order` row (ADR-0007) — so it **cannot** be cancelled/modified through `/orders/{id}`, and a
   * control offering it would not route. The blotter renders such an order without an actionable cancel.
   */
  readonly orderId: string | null;
  readonly contract: string;
  readonly stopPrice: number | null;
  readonly limitPrice: number | null;
  readonly size: number;
  /** True when it carries a stop — i.e. it is a protective leg actually standing at the venue. */
  readonly isProtective: boolean;
}

/**
 * A venue-truth view. `unknown` deliberately has **no** `items`: there is nothing trustworthy to show, and
 * offering an empty list would let a caller render it as "none".
 */
export type VenueView<T> =
  | { readonly basis: 'live'; readonly items: readonly T[] }
  | { readonly basis: 'settlement'; readonly items: readonly T[] }
  | { readonly basis: 'unknown' };

/** The wire shape, before the basis is resolved. */
interface RawView<T> {
  readonly markBasis?: string;
  readonly positions?: readonly T[];
  readonly orders?: readonly T[];
}

/**
 * Resolves the wire shape into a view. Anything that is not a recognised trustworthy basis becomes `unknown` and
 * loses its list — including `Unknown` itself, an unrecognised value, and a missing field.
 */
function toView<T>(raw: RawView<T>, items: readonly T[] | undefined): VenueView<T> {
  switch (raw.markBasis) {
    case 'Live':
      return { basis: 'live', items: items ?? [] };
    case 'Settlement':
      // A re-mark inside the maintenance window: not live movement, and not a fault. Its own rendering.
      return { basis: 'settlement', items: items ?? [] };
    default:
      return { basis: 'unknown' };
  }
}

/** The account's positions from venue truth. */
export async function getPositions(
  accountId: string,
): Promise<ApiResult<VenueView<BlotterPosition>>> {
  const result = await request<RawView<BlotterPosition>>('GET', `/accounts/${accountId}/positions`);

  return result.ok ? { ok: true, data: toView(result.data, result.data.positions) } : result;
}

/** The account's resting orders from venue truth. */
export async function getRestingOrders(
  accountId: string,
): Promise<ApiResult<VenueView<BlotterRestingOrder>>> {
  const result = await request<RawView<BlotterRestingOrder>>(
    'GET',
    `/accounts/${accountId}/orders`,
  );

  return result.ok ? { ok: true, data: toView(result.data, result.data.orders) } : result;
}

/**
 * What an exit achieved. Only `Flat` is success — see {@link exitPosition}.
 */
export interface PositionExitResult {
  readonly outcome: string;
  /** The signed exposure the venue still reports; 0 when flat. */
  readonly netQuantity: number;
}

/**
 * Closes one instrument's position at market (gh#656).
 *
 * **Only a verified flat is a success.** The server answers 409 when the close was accepted but the venue still
 * reports exposure (`StillOpen`), and 409 again when the venue could not be reached (`Unreachable`) — both carry
 * a body. Treating either as done would stop the operator watching a position that is still live, so a failure
 * here is rendered, never swallowed.
 */
export function exitPosition(
  accountId: string,
  instrument: string,
): Promise<ApiResult<PositionExitResult>> {
  return request<PositionExitResult>(
    'POST',
    `/accounts/${accountId}/positions/${encodeURIComponent(instrument)}/exit`,
  );
}
