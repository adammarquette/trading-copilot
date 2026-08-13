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

/**
 * What a reprice may move. **Partial by design** — send only what the operator changed; naming a field is a
 * request to move it, so filling the rest with nulls would read as a request to clear them.
 *
 * The **safety stop is not here**, and cannot be: it stays invariant on every reprice path server-side, so the
 * R-5 catastrophic floor is untouched by anything this surface can do.
 */
export interface RepriceRequest {
  readonly entryPrice?: number;
  readonly workingStopPrice?: number;
  readonly size?: number;
}

/**
 * Moves a resting working order in place (gh#259/#267/#278/#292) — keeping its queue position and its attached
 * protective bracket, rather than cancel-and-replace.
 *
 * Unlike a cancel, a reprice **can add risk** (a wider stop, an entry likelier to fill), so the server runs the
 * full send ladder and **re-gates** it before touching the venue. A refusal is the gate's answer, not an error to
 * swallow — it reaches the operator.
 */
export function repriceOrder(orderId: string, change: RepriceRequest): Promise<ApiResult<void>> {
  return request<void>('PATCH', `/orders/${orderId}/price`, change);
}

/**
 * Which way price must cross the trigger for a conditional entry to fire (ADR-0007, gh#176). Serialized as its
 * integer — the values must match the domain enum exactly. {@link ConditionalCrossDirection.Unknown} is the
 * **refusable zero**: the unset value never fires, so a conditional must declare a real direction or it would rest
 * forever.
 */
export const ConditionalCrossDirection = { Unknown: 0, RisesTo: 1, FallsTo: 2 } as const;
export type ConditionalCrossDirection =
  (typeof ConditionalCrossDirection)[keyof typeof ConditionalCrossDirection];

/**
 * The request to create a **synthetic conditional** entry (gh#176, ADR-0007) — the "send when conditions met" mode.
 * The proposal rides an ordinary {@link SendOrderRequest}; the conditional part is the trigger and its optional
 * cancel band / expiry. Nothing transmits — the order is held local and fired by a watcher when the trigger crosses.
 */
export interface CreateConditionalOrderRequest {
  readonly order: SendOrderRequest;
  /** The price at which the order fires. */
  readonly triggerPrice: number;
  /** Which way price must cross the trigger to fire. Travels as its integer; {@link ConditionalCrossDirection.Unknown} is refused. */
  readonly triggerDirection: ConditionalCrossDirection;
  /** The optional cancel band — the stale-side price past which the setup is abandoned. */
  readonly cancelDrift?: number | null;
  /** The optional validity deadline (ISO-8601); past it, a still-pending order cancels. */
  readonly expiresAt?: string | null;
}

/**
 * A created, **pending** conditional and the gate's decision *at creation time*.
 *
 * The decision (`outcome` / `approvedQuantity` / `bindingLayer` / `reason`) is a **preview**, not a commitment: the
 * authoritative gate re-runs at fire time (R-12), so this can only ever be indicative. `status` is `Pending` and
 * `triggerDirection` comes back as a **name** (`RisesTo` / `FallsTo`), the same asymmetry as `outcome`.
 */
export interface ConditionalOrderResponse {
  readonly conditionalOrderId: string;
  readonly status: string;
  readonly triggerPrice: number;
  readonly triggerDirection: string;
  /** The preview outcome at creation — re-decided when the trigger fires. */
  readonly outcome: string;
  readonly approvedQuantity: number;
  readonly bindingLayer: number | null;
  readonly reason: string;
}

/**
 * Creates a "send when conditions met" order — held local, off the book, fired by a watcher when the trigger
 * crosses. **Transmits nothing now**; the gate re-runs at fire time (R-12). A pre-gate refusal comes back as a
 * failed result to render, not an error to swallow.
 */
export function createConditionalOrder(
  accountId: string,
  request_: CreateConditionalOrderRequest,
): Promise<ApiResult<ConditionalOrderResponse>> {
  return request<ConditionalOrderResponse>(
    'POST',
    `/accounts/${accountId}/orders/conditional`,
    request_,
  );
}

/**
 * Withdraws a **pending** conditional — the operator's cancel of a "send when conditions met" entry (gh#655). It is
 * held local (off the book), so this is a plain server-side delete; nothing reaches the venue. Only a Pending
 * conditional can be withdrawn — a mid-fire one is a maybe-live entry the server refuses (409), which comes back as
 * an ordinary result to render, not an error to swallow.
 */
export function cancelConditionalOrder(conditionalOrderId: string): Promise<ApiResult<void>> {
  return request<void>('DELETE', `/conditionals/${conditionalOrderId}`);
}
