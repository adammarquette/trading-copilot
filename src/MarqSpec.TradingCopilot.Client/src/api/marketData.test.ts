import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./client', () => ({ request: vi.fn() }));

import { request } from './client';
import { getBars } from './marketData';

const requestMock = vi.mocked(request);

beforeEach(() => {
  requestMock.mockReset();
});

describe('getBars', () => {
  it('reads the bars route with the venue/instrument/resolution/window as query params', async () => {
    requestMock.mockResolvedValue({
      ok: true,
      data: { venue: 'topstepx', instrument: 'ES', resolution: 1, points: [] },
    });

    await getBars('topstepx', 'ES', 1, '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z');

    expect(requestMock).toHaveBeenCalledOnce();
    const [method, path] = requestMock.mock.calls[0];
    expect(method).toBe('GET');
    const url = new URL(path, 'http://local');
    expect(url.pathname).toBe('/api/marketdata/bars');
    expect(url.searchParams.get('venue')).toBe('topstepx');
    expect(url.searchParams.get('instrument')).toBe('ES');
    expect(url.searchParams.get('resolution')).toBe('1');
    expect(url.searchParams.get('from')).toBe('2026-01-01T00:00:00Z');
    expect(url.searchParams.get('to')).toBe('2026-01-02T00:00:00Z');
  });

  it('returns the bar series on success', async () => {
    const series = {
      venue: 'topstepx',
      instrument: 'ES',
      resolution: 1,
      points: [
        { bucketStart: '2026-01-01T00:00:00Z', open: 5300, high: 5310, low: 5295, close: 5308, volume: 1200 },
      ],
    };
    requestMock.mockResolvedValue({ ok: true, data: series });

    await expect(getBars('topstepx', 'ES', 1, 'a', 'b')).resolves.toEqual({ ok: true, data: series });
  });

  it('passes a refusal through unchanged (the window-too-wide bound is the gate answer, not an error)', async () => {
    requestMock.mockResolvedValue({ ok: false, kind: 'refused', status: 400, reason: 'window too wide' });

    await expect(getBars('topstepx', 'ES', 1, 'a', 'b')).resolves.toEqual({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'window too wide',
    });
  });
});
