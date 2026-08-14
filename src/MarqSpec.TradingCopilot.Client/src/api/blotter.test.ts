import { afterEach, describe, expect, it, vi } from 'vitest';

import { exitPosition, getPositions, getRestingOrders } from './blotter';

function response(status: number, body?: unknown): Response {
  const text = body === undefined ? '' : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    text: () => Promise.resolve(text),
  } as unknown as Response;
}

// The house shape (see api/flatten.test.ts): taking a TYPED impl gives the mock fetch's parameter types, so
// `mock.calls[0][0]` is the requested URL -- a no-arg lambda would leave it an empty tuple.
function stubFetch(impl: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>) {
  const mock = vi.fn(impl);
  vi.stubGlobal('fetch', mock);
  return mock;
}

function stubJson(body: unknown, status = 200) {
  return stubFetch(() => Promise.resolve(response(status, body)));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe('getPositions', () => {
  it('reports a live view with its positions', async () => {
    stubJson({
      markBasis: 'Live',
      positions: [
        { contract: 'CON.F.US.MES.U26', netQuantity: 2, averagePrice: 5000, isFlat: false },
      ],
    });

    const result = await getPositions('a1');

    expect(result.ok).toBe(true);
    if (result.ok && result.data.basis !== 'unknown') {
      expect(result.data.basis).toBe('live');
      expect(result.data.items).toHaveLength(1);
    }
  });

  it('keeps a settlement re-mark distinguishable from live', async () => {
    // A settlement mark is a legitimate re-mark inside the maintenance window -- not live movement, and not a
    // fault. Three renderings, not two plus an error.
    stubJson({ markBasis: 'Settlement', positions: [] });

    const result = await getPositions('a1');

    expect(result.ok && result.data.basis).toBe('settlement');
  });

  it('carries NO items at all when the basis is unknown', async () => {
    // The structural half of this module. The server sends an empty array on Unknown, which is
    // indistinguishable from genuinely flat -- so the unknown view drops the field entirely and a surface cannot
    // iterate what is not there. TypeScript refuses `data.items` on this branch; this pins it at runtime too.
    stubJson({ markBasis: 'Unknown', positions: [] });

    const result = await getPositions('a1');

    expect(result.ok && result.data.basis).toBe('unknown');
    expect(result.ok && 'items' in result.data).toBe(false);
  });

  it('distinguishes genuinely flat from unreachable', async () => {
    // Both arrive as an empty array. Only the basis separates "you have no positions" from "we could not ask",
    // and rendering the second as the first would show FLAT during an outage -- which reads as safe.
    stubJson({ markBasis: 'Live', positions: [] });

    const result = await getPositions('a1');

    expect(result.ok && result.data.basis).toBe('live');
    if (result.ok && result.data.basis === 'live') {
      expect(result.data.items).toEqual([]);
    }
  });

  it('treats an unrecognised basis as unknown, never as live', async () => {
    // Fail toward declared-unknown (ADR-0013). A basis this client does not recognise -- a server that grew a
    // fourth value, a truncated response -- must not default to the one reading that implies the view is
    // trustworthy.
    stubJson({
      markBasis: 'SomethingNew',
      positions: [{ contract: 'ES', netQuantity: 9, averagePrice: 1, isFlat: false }],
    });

    const result = await getPositions('a1');

    expect(result.ok && result.data.basis).toBe('unknown');
  });

  it('treats a missing basis as unknown', async () => {
    stubJson({ positions: [] });

    expect((await getPositions('a1')).ok && (await getPositions('a1')).ok).toBe(true);
    const result = await getPositions('a1');
    expect(result.ok && result.data.basis).toBe('unknown');
  });

  it('surfaces a failed read rather than an empty view', async () => {
    stubJson(undefined, 503);

    expect((await getPositions('a1')).ok).toBe(false);
  });
});

describe('getRestingOrders', () => {
  it('carries the size the read actually provides', async () => {
    // gh#656's card says the read drops size. It does not -- RestingOrder carries `size`, so the blotter shows
    // it rather than caveating it.
    stubJson({
      markBasis: 'Live',
      orders: [
        {
          venueOrderKey: 'v1',
          orderId: 'ord-1',
          contract: 'CON.F.US.MES.U26',
          stopPrice: 4990,
          limitPrice: null,
          size: 2,
          isProtective: true,
        },
      ],
    });

    const result = await getRestingOrders('a1');

    if (result.ok && result.data.basis === 'live') {
      expect(result.data.items[0].size).toBe(2);
      expect(result.data.items[0].isProtective).toBe(true);
      // gh#656: the journaled app Order.Id the write endpoints route on, surfaced alongside the venue key.
      expect(result.data.items[0].orderId).toBe('ord-1');
    }
  });

  it('surfaces a null orderId for an unjournaled leg', async () => {
    // A venue-spawned bracket leg has no journaled Order row, so the read carries orderId: null — the blotter
    // renders it as not-actionable rather than offering a cancel that /orders/{id} could not route.
    stubJson({
      markBasis: 'Live',
      orders: [
        {
          venueOrderKey: 'v2',
          orderId: null,
          contract: 'CON.F.US.MES.U26',
          stopPrice: 4980,
          limitPrice: null,
          size: 1,
          isProtective: true,
        },
      ],
    });

    const result = await getRestingOrders('a1');

    if (result.ok && result.data.basis === 'live') {
      expect(result.data.items[0].orderId).toBeNull();
    }
  });

  it('carries no orders when the basis is unknown', async () => {
    stubJson({ markBasis: 'Unknown', orders: [] });

    const result = await getRestingOrders('a1');

    expect(result.ok && result.data.basis).toBe('unknown');
    expect(result.ok && 'items' in result.data).toBe(false);
  });

  it('requests the account-scoped orders route', async () => {
    const fetchMock = stubJson({ markBasis: 'Live', orders: [] });

    await getRestingOrders('a1');

    expect(String(fetchMock.mock.calls[0][0])).toContain('/accounts/a1/orders');
  });
});

describe('exitPosition', () => {
  it('posts to the account-and-instrument exit route', async () => {
    const fetchMock = stubJson({ outcome: 'Flat', netQuantity: 0 });

    await exitPosition('a1', 'MES');

    expect(String(fetchMock.mock.calls[0][0])).toContain('/accounts/a1/positions/MES/exit');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('POST');
  });

  it('encodes an instrument that is not URL-safe', async () => {
    // Venue contract keys carry dots and can carry slashes; an unencoded one would silently change the route.
    const fetchMock = stubJson({ outcome: 'Flat', netQuantity: 0 });

    await exitPosition('a1', 'CON.F.US/MES.U26');

    expect(String(fetchMock.mock.calls[0][0])).toContain('CON.F.US%2FMES.U26');
  });

  it('surfaces a still-open exit as a failure, not a success', async () => {
    // The server answers 409 when the venue still reports exposure. Reporting that as done would stop the
    // operator watching a position that is still live.
    stubJson({ outcome: 'StillOpen', netQuantity: 2 }, 409);

    expect((await exitPosition('a1', 'MES')).ok).toBe(false);
  });
});
