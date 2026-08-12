import { afterEach, describe, expect, it, vi } from 'vitest';

import { RiskLayer } from '../orders/gateDecision';
import {
  OrderSide,
  OrderType,
  type SendOrderRequest,
  armOrder,
  cancelOrder,
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
