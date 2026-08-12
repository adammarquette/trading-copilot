import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./client', () => ({ request: vi.fn() }));

import { request } from './client';
import { getPositions, getWorkingOrders } from './execution';

const requestMock = vi.mocked(request);

beforeEach(() => {
  requestMock.mockReset();
});

describe('getWorkingOrders', () => {
  it('reads the account orders route scoped to the instrument (gh#772)', async () => {
    requestMock.mockResolvedValue({ ok: true, data: { markBasis: 'Live', orders: [] } });

    await getWorkingOrders('acc-1', 'ES');

    expect(requestMock).toHaveBeenCalledOnce();
    const [method, path] = requestMock.mock.calls[0];
    expect(method).toBe('GET');
    const url = new URL(path, 'http://local');
    expect(url.pathname).toBe('/accounts/acc-1/orders');
    expect(url.searchParams.get('instrument')).toBe('ES');
  });

  it('returns the resting orders on success', async () => {
    const data = {
      markBasis: 'Live',
      orders: [
        {
          venueOrderKey: 'o1',
          contract: 'CON.F.US.MES.U26',
          stopPrice: 5290,
          limitPrice: null,
          size: 2,
          isProtective: true,
        },
      ],
    };
    requestMock.mockResolvedValue({ ok: true, data });

    await expect(getWorkingOrders('acc-1', 'ES')).resolves.toEqual({ ok: true, data });
  });

  it('passes a refusal through unchanged (an unresolvable instrument is a 400 gate answer, not an error)', async () => {
    const refusal = {
      ok: false,
      kind: 'refused',
      status: 400,
      reason: "No contract matches instrument 'ZZZZ'.",
    } as const;
    requestMock.mockResolvedValue(refusal);

    await expect(getWorkingOrders('acc-1', 'ZZZZ')).resolves.toEqual(refusal);
  });
});

describe('getPositions', () => {
  it('reads the account positions route scoped to the instrument (gh#772)', async () => {
    requestMock.mockResolvedValue({ ok: true, data: { markBasis: 'Live', positions: [] } });

    await getPositions('acc-1', 'ES');

    expect(requestMock).toHaveBeenCalledOnce();
    const [method, path] = requestMock.mock.calls[0];
    expect(method).toBe('GET');
    const url = new URL(path, 'http://local');
    expect(url.pathname).toBe('/accounts/acc-1/positions');
    expect(url.searchParams.get('instrument')).toBe('ES');
  });

  it('returns the reconciled positions on success', async () => {
    const data = {
      markBasis: 'Live',
      positions: [
        { contract: 'CON.F.US.MES.U26', netQuantity: 2, averagePrice: 5300, isFlat: false },
      ],
    };
    requestMock.mockResolvedValue({ ok: true, data });

    await expect(getPositions('acc-1', 'ES')).resolves.toEqual({ ok: true, data });
  });
});
