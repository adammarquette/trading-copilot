import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./client', () => ({ request: vi.fn(), requestJson: vi.fn() }));

import { requestJson } from './client';
import { getFills } from './fills';

const requestJsonMock = vi.mocked(requestJson);

beforeEach(() => {
  requestJsonMock.mockReset();
});

describe('getFills', () => {
  it('reads the account fills route scoped to the instrument and window (gh#792)', async () => {
    requestJsonMock.mockResolvedValue({ ok: true, data: { fills: [] } });

    await getFills('acc-1', 'ES', '2026-07-28T00:00:00Z', '2026-07-29T00:00:00Z');

    expect(requestJsonMock).toHaveBeenCalledOnce();
    const [method, path] = requestJsonMock.mock.calls[0];
    expect(method).toBe('GET');
    const url = new URL(path, 'http://local');
    expect(url.pathname).toBe('/accounts/acc-1/fills');
    expect(url.searchParams.get('instrument')).toBe('ES');
    expect(url.searchParams.get('from')).toBe('2026-07-28T00:00:00Z');
    expect(url.searchParams.get('to')).toBe('2026-07-29T00:00:00Z');
  });

  it('returns the fills on success', async () => {
    const data = {
      fills: [
        {
          venueFillKey: 'f1',
          side: 'Buy',
          price: 5300,
          size: 2,
          fees: 1.25,
          executedAt: '2026-07-28T15:00:00Z',
        },
      ],
    };
    requestJsonMock.mockResolvedValue({ ok: true, data });

    await expect(getFills('acc-1', 'ES', 'a', 'b')).resolves.toEqual({ ok: true, data });
  });

  it('passes a refusal through unchanged (an over-wide window is a 400 gate answer, not an error)', async () => {
    const refusal = {
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'the window returns more than 1000 fills; narrow it.',
    } as const;
    requestJsonMock.mockResolvedValue(refusal);

    await expect(getFills('acc-1', 'ES', 'a', 'b')).resolves.toEqual(refusal);
  });
});
