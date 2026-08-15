import { afterEach, describe, expect, it, vi } from 'vitest';

import { RiskLayer } from '../orders/gateDecision';
import {
  ConditionalCrossDirection,
  OrderSide,
  OrderType,
  type SendOrderRequest,
  armOrder,
  cancelConditionalOrder,
  cancelOrder,
  createConditionalOrder,
  editStagedOrder,
  repriceOrder,
  sendOrder,
  takeStagedOrder,
} from './orders';

function response(status: number, body?: unknown): Response {
  const text = body === undefined ? '' : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    text: () => Promise.resolve(text),
  } as unknown as Response;
}

function stubFetch(impl: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>) {
  const mock = vi.fn(impl);
  vi.stubGlobal('fetch', mock);
  return mock;
}

/** A neutral long MES ticket. Every price the server needs is supplied by the caller -- there is no server-side
 *  price read on this path, so `referencePrice` travels with the request. */
const TICKET: SendOrderRequest = {
  symbol: 'MES',
  tickSize: 0.25,
  pointValue: 5,
  side: OrderSide.Buy,
  quantity: 5,
  entry: 5000,
  stop: 4995,
  safetyStop: 4990,
  referencePrice: 5000,
  type: OrderType.Market,
  target: 5020,
};

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe('sendOrder', () => {
  it('reports the gate decision, with the binding layer as its integer', async () => {
    // The asymmetry this module exists to absorb: `outcome` arrives as a NAME, `bindingLayer` as its INTEGER --
    // there is no JsonStringEnumConverter server-side. The layer stays numeric here and is named at the
    // presentation seam (orders/gateDecision), so exactly one place owns the mapping.
    stubFetch(() =>
      Promise.resolve(
        response(200, {
          outcome: 'Resized',
          orderId: 'o1',
          venueOrderKey: null,
          approvedQuantity: 2,
          bindingLayer: RiskLayer.DailyGovernor,
          reason: 'Daily governor leaves room for 2.',
          advisories: [],
        }),
      ),
    );

    const result = await sendOrder('a1', TICKET);

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.data.outcome).toBe('Resized');
      expect(result.data.approvedQuantity).toBe(2);
      expect(result.data.bindingLayer).toBe(2);
    }
  });

  it('carries a pre-gate refusal through as an outcome with no decision to read', async () => {
    // A pre-gate refusal never sized anything, so there is no binding layer and approvedQuantity is 0. The
    // surface must read the OUTCOME rather than infer a reason from the absent decision (gh#655).
    stubFetch(() =>
      Promise.resolve(
        response(200, {
          outcome: 'RefusedByKillSwitch',
          orderId: null,
          venueOrderKey: null,
          approvedQuantity: 0,
          bindingLayer: null,
          reason: 'The kill switch is engaged.',
          advisories: [],
        }),
      ),
    );

    const result = await sendOrder('a1', TICKET);

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.data.outcome).toBe('RefusedByKillSwitch');
      expect(result.data.bindingLayer).toBeNull();
      expect(result.data.reason).not.toBe('');
    }
  });

  it('posts to the account-scoped send route', async () => {
    const fetchMock = stubFetch(() =>
      Promise.resolve(
        response(200, {
          outcome: 'Allowed',
          orderId: 'o1',
          venueOrderKey: 'v1',
          approvedQuantity: 5,
          bindingLayer: null,
          reason: 'ok',
          advisories: [],
        }),
      ),
    );

    await sendOrder('a1', TICKET);

    expect(String(fetchMock.mock.calls[0][0])).toContain('/accounts/a1/orders');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('POST');
  });

  it('surfaces a refusal rather than reporting a send', async () => {
    stubFetch(() => Promise.resolve(response(409, { error: 'account not tradable' })));

    expect((await sendOrder('a1', TICKET)).ok).toBe(false);
  });
});

describe('armOrder', () => {
  it('stages without transmitting, and returns the decision to review', async () => {
    // Arm -> review -> send stays three steps (R-11). Arming is the step that must NOT reach the venue: the
    // response carries a staged order id and a gate decision, never a venue order key.
    const fetchMock = stubFetch(() =>
      Promise.resolve(
        response(200, {
          orderId: 'o1',
          status: 'Staged',
          outcome: 'Resized',
          approvedQuantity: 2,
          bindingLayer: RiskLayer.PerTradeRisk,
          reason: 'Per-trade risk allows 2.',
          target: 5020,
          advisories: [],
        }),
      ),
    );

    const result = await armOrder('a1', TICKET);

    expect(String(fetchMock.mock.calls[0][0])).toContain('/accounts/a1/orders/arm');
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.data.status).toBe('Staged');
      expect(result.data.bindingLayer).toBe(4);
    }
  });
});

describe('editStagedOrder', () => {
  it('PUTs the whole edited proposal and returns the RE-GATED decision', async () => {
    // An edit re-gates (ADR-0007). What transmits is the size the gate approved on the EDITED row, never the
    // pre-edit approval — so the response carries a fresh decision, and a caller that kept the old one would
    // show an approval that no longer describes what would be sent.
    const fetchMock = stubFetch(() =>
      Promise.resolve(
        response(200, {
          orderId: 'o1',
          status: 'Staged',
          outcome: 'Resized',
          approvedQuantity: 3,
          bindingLayer: RiskLayer.PerTradeRisk,
          reason: 'Per-trade risk allows 3.',
          target: 5020,
          advisories: [],
        }),
      ),
    );

    const result = await editStagedOrder('o1', { ...TICKET, quantity: 4 });

    expect(String(fetchMock.mock.calls[0][0])).toContain('/orders/o1');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('PUT');
    // Whole, not partial: the server rebuilds the ticket from these fields, so an omitted one is not "unchanged".
    // The safety stop rides along on every edit — the R-5 floor is never dropped by an amendment.
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toMatchObject({
      quantity: 4,
      safetyStop: 4990,
      referencePrice: 5000,
    });
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.data.approvedQuantity).toBe(3);
    }
  });

  it('renders a refusal when the order has already left staging', async () => {
    // The row can go Working / Taking between the review and the amendment. That 409 is an ANSWER — the order
    // exists and may be live — so it must reach the operator as a refusal, never as a retryable failure.
    stubFetch(() =>
      Promise.resolve(
        response(409, { error: 'Only a staged order can be edited — this one has left staging.' }),
      ),
    );

    const result = await editStagedOrder('o1', TICKET);

    expect(result.ok).toBe(false);
    if (!result.ok && result.kind === 'refused') {
      expect(result.reason).toContain('left staging');
    } else {
      expect.unreachable('a 409 with an error body is a refusal, not a failure');
    }
  });
});

describe('takeStagedOrder', () => {
  it('sends a staged order by id', async () => {
    const fetchMock = stubFetch(() =>
      Promise.resolve(
        response(200, {
          outcome: 'Allowed',
          orderId: 'o1',
          venueOrderKey: 'v1',
          approvedQuantity: 2,
          bindingLayer: null,
          reason: 'ok',
          advisories: [],
        }),
      ),
    );

    await takeStagedOrder('o1');

    expect(String(fetchMock.mock.calls[0][0])).toContain('/orders/o1/take');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('POST');
  });
});

describe('cancelOrder', () => {
  it('deletes the order', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(204)));

    await cancelOrder('o1');

    expect(String(fetchMock.mock.calls[0][0])).toContain('/orders/o1');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('DELETE');
  });
});

function pending(body: Record<string, unknown> = {}): Response {
  return response(200, {
    conditionalOrderId: 'c1',
    status: 'Pending',
    triggerPrice: 5010,
    triggerDirection: 'RisesTo',
    outcome: 'Allowed',
    approvedQuantity: 5,
    bindingLayer: null,
    reason: 'Within every layer at creation.',
    ...body,
  });
}

describe('ConditionalCrossDirection', () => {
  it('matches the server enum — Unknown is the refusable zero that never fires', () => {
    // The direction travels as its INTEGER (there is no JsonStringEnumConverter server-side), so the values must
    // match the domain enum exactly: a positional slip would fire a breakout as a pullback. Unknown=0 never fires.
    expect(ConditionalCrossDirection.Unknown).toBe(0);
    expect(ConditionalCrossDirection.RisesTo).toBe(1);
    expect(ConditionalCrossDirection.FallsTo).toBe(2);
  });
});

describe('createConditionalOrder', () => {
  it('posts the proposal plus the trigger to the account-scoped conditional route', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(pending()));

    const result = await createConditionalOrder('a1', {
      order: TICKET,
      triggerPrice: 5010,
      triggerDirection: ConditionalCrossDirection.RisesTo,
    });

    expect(String(fetchMock.mock.calls[0][0])).toContain('/accounts/a1/orders/conditional');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('POST');
    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body));
    expect(body.triggerPrice).toBe(5010);
    expect(body.triggerDirection).toBe(ConditionalCrossDirection.RisesTo); // 1 — the integer, not the name
    expect(body.order.symbol).toBe('MES');
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.data.status).toBe('Pending');
      expect(result.data.triggerDirection).toBe('RisesTo'); // a NAME on the way back, like every outcome
    }
  });

  it('carries the optional cancel band and expiry when the operator sets them', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(pending()));

    await createConditionalOrder('a1', {
      order: TICKET,
      triggerPrice: 5010,
      triggerDirection: ConditionalCrossDirection.RisesTo,
      cancelDrift: 4990,
      expiresAt: '2026-08-13T00:00:00Z',
    });

    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body));
    expect(body.cancelDrift).toBe(4990);
    expect(body.expiresAt).toBe('2026-08-13T00:00:00Z');
  });

  it('surfaces a refusal rather than a pending conditional', async () => {
    // A pre-gate refusal (mode, mismatch, wrong-side target) means there is nothing coherent to hold, so the create
    // comes back 4xx — an ordinary result to render, not a pending order to show.
    stubFetch(() =>
      Promise.resolve(
        response(409, { error: 'this environment will not trade that account mode' }),
      ),
    );

    const result = await createConditionalOrder('a1', {
      order: TICKET,
      triggerPrice: 5010,
      triggerDirection: ConditionalCrossDirection.RisesTo,
    });

    expect(result.ok).toBe(false);
  });
});

describe('repriceOrder', () => {
  it('patches the order price', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(200, { orderId: 'o1' })));

    await repriceOrder('o1', { entryPrice: 5010 });

    expect(String(fetchMock.mock.calls[0][0])).toContain('/orders/o1/price');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('PATCH');
  });

  it('sends only the fields the operator changed', async () => {
    // The safety stop stays invariant on every reprice path server-side, and a partial PATCH is how the caller
    // says "move the entry, leave everything else" -- sending nulls for the rest would read as a request to
    // clear them.
    const fetchMock = stubFetch(() => Promise.resolve(response(200, { orderId: 'o1' })));

    await repriceOrder('o1', { workingStopPrice: 4996 });

    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body)) as Record<string, unknown>;
    expect(body).toEqual({ workingStopPrice: 4996 });
  });

  it('carries the reference price on an entry move — the fat-finger band re-measures against it', async () => {
    // An entry reprice re-gates (R-16), and the server has no quote read on this path, so the caller supplies the
    // current market price. It must travel with the entry, or the server refuses the move for want of it.
    const fetchMock = stubFetch(() => Promise.resolve(response(200, { orderId: 'o1' })));

    await repriceOrder('o1', { entryPrice: 5010, referencePrice: 5008 });

    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body)) as Record<string, unknown>;
    expect(body).toEqual({ entryPrice: 5010, referencePrice: 5008 });
  });

  it('surfaces a refusal rather than reporting a move', async () => {
    // A reprice CAN add risk (a wider stop, an entry likelier to fill), so it runs the full send ladder and is
    // re-gated. A refusal is the gate's answer and must reach the operator.
    stubFetch(() => Promise.resolve(response(422, { error: 'would breach the drawdown floor' })));

    expect((await repriceOrder('o1', { entryPrice: 5010 })).ok).toBe(false);
  });
});

describe('cancelConditionalOrder', () => {
  it('deletes the conditional by its own id — the operator withdrawal', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(200, { status: 'Cancelled' })));

    await cancelConditionalOrder('c1');

    expect(String(fetchMock.mock.calls[0][0])).toContain('/conditionals/c1');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('DELETE');
  });

  it('surfaces a refusal — a mid-fire conditional cannot be withdrawn', async () => {
    // Only a Pending conditional is cancellable; a Firing one is a maybe-live entry the server refuses (409). The
    // refusal is an ordinary result to render, not an error to swallow.
    stubFetch(() =>
      Promise.resolve(
        response(409, {
          error: 'Only a pending conditional can be cancelled — this one is Firing.',
        }),
      ),
    );

    const result = await cancelConditionalOrder('c1');

    expect(result.ok).toBe(false);
  });
});
