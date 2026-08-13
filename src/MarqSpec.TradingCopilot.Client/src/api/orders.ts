import { type ApiResult, request } from './client';

/**
 * The order paths behind the ticket (gh#655, R-11 / R-12, ADR-0007).
 *
 * **Arm → review → send is three steps, and this module keeps them three.** `armOrder` stages and returns the
 * gate's decision without reaching the venue; `takeStagedOrder` is the separate, explicit transmission. That
 * opt-in posture is R-11's and is what makes an edited take safe — do not add a convenience call that collapses
 * them.
 *
 * **Enums travel as integers, outcomes as names.** There is no `JsonStringEnumConverter` server-side, so `side`
 * and `type` go out as numbers, while `outcome` comes back as a string and `bindingLayer` as a number. The layer
 * is deliberately left numeric here and named at the presentation seam
 * ({@link ../orders/gateDecision.riskLayerName}), so exactly one place owns that mapping.
 *
 * **Every price is caller-supplied.** There is no server-side price read on this path, so `referencePrice`
 * travels with the request alongside the instrument's `tickSize` / `pointValue` and the mandatory `safetyStop` —
 * the "always insured" floor the ticket must show on every order.
 */

/** Which way the order goes. Serialized as its integer. */
export const OrderSide = { Buy: 0, Sell: 1 } as const;
export type OrderSide = (typeof OrderSide)[keyof typeof OrderSide];

/**
 * The order types. Serialized as its integer.
 *
 * `TrailingStop` is listed for completeness of the wire contract but is **refused outright** by the send path —
 * the neutral ticket carries no trail distance. Do not offer it as a selectable type: a control that always fails
 * is worse than an absent one (gh#655).
 */
export const OrderType = { Market: 0, Limit: 1, Stop: 2, StopLimit: 3, TrailingStop: 4 } as const;
export type OrderType = (typeof OrderType)[keyof typeof OrderType];

/** The types a ticket may actually offer — {@link OrderType.TrailingStop} is excluded by construction. */
export const SELECTABLE_ORDER_TYPES: readonly OrderType[] = [
  OrderType.Market,
  OrderType.Limit,
  OrderType.Stop,
  OrderType.StopLimit,
];

/** An entry proposal. The gate sizes it; the ticket never assumes the requested quantity will be transmitted. */
export interface SendOrderRequest {
  readonly symbol: string;
  readonly tickSize: number;
  readonly pointValue: number;
  readonly side: OrderSide;
  readonly quantity: number;
  readonly entry: number;
  readonly stop: number;
  /** The catastrophic floor that always rests at the venue — R-5's "insured" promise, never optional. */
  readonly safetyStop: number;
  /** The caller's price of record; there is no server-side read on this path. */
  readonly referencePrice: number;
  readonly type: OrderType;
  readonly target?: number | null;
}

/** What a send did, and why. `reason` is always populated (R-5) — the operator is never told "no" without a why. */
export interface SendOrderResponse {
  /** `Allowed` / `Resized` / `Blocked`, or a pre-gate refusal such as `RefusedByKillSwitch`. A name, not a number. */
  readonly outcome: string;
  readonly orderId: string | null;
  readonly venueOrderKey: string | null;
  /** The contracts the gate authorized — 0 when blocked or never sized. This is what actually transmits. */
  readonly approvedQuantity: number;
  /** The bound risk layer as its **integer**, or `null` — including on every pre-gate refusal, which sized nothing. */
  readonly bindingLayer: number | null;
  readonly reason: string;
  readonly advisories: readonly unknown[];
}

/** A staged (armed) order with the decision to review before sending. */
export interface StagedOrderResponse {
  readonly orderId: string;
  readonly status: string;
  readonly outcome: string;
  readonly approvedQuantity: number;
  readonly bindingLayer: number | null;
  readonly reason: string;
  readonly target: number | null;
  readonly advisories: readonly unknown[];
}

/** Sends an entry through the gate. A refusal is an ordinary result to render, not an error to swallow. */
export function sendOrder(
  accountId: string,
  order: SendOrderRequest,
): Promise<ApiResult<SendOrderResponse>> {
  return request<SendOrderResponse>('POST', `/accounts/${accountId}/orders`, order);
}

/** Stages an order and returns the gate's decision — **does not transmit**. Step one of arm → review → send. */
export function armOrder(
  accountId: string,
  order: SendOrderRequest,
): Promise<ApiResult<StagedOrderResponse>> {
  return request<StagedOrderResponse>('POST', `/accounts/${accountId}/orders/arm`, order);
}

/** Transmits a staged order — the separate, explicit third step. */
export function takeStagedOrder(orderId: string): Promise<ApiResult<SendOrderResponse>> {
  return request<SendOrderResponse>('POST', `/orders/${orderId}/take`);
}

/** Cancels a staged or working order. */
export function cancelOrder(orderId: string): Promise<ApiResult<void>> {
  return request<void>('DELETE', `/orders/${orderId}`);
}
