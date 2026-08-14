import { afterEach, describe, expect, it, vi } from 'vitest';

import { type AiSpend, getAiSpend } from './ai';

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

const SPEND: AiSpend = {
  from: '2026-07-01T05:00:00Z',
  to: '2026-07-15T18:00:00Z',
  totalUsd: 34.1,
  todayUsd: 1.2,
  dailyBudgetUsd: 5,
  byModel: [
    { model: 'claude-opus-5', costUsd: 20 },
    { model: 'claude-haiku-4-5', costUsd: 14.1 },
  ],
  byDay: [
    { day: '2026-07-14', costUsd: 10 },
    { day: '2026-07-15', costUsd: 24.1 },
  ],
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ai spend client', () => {
  it('reads spend from /api/ai/spend with no bounds by default', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, SPEND)));

    const result = await getAiSpend();

    expect(mock.mock.calls[0][0]).toBe('/api/ai/spend');
    expect(result).toEqual({ ok: true, data: SPEND });
  });

  it('passes from/to as query params when given', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, SPEND)));

    await getAiSpend('2026-07-01T00:00:00Z', '2026-07-31T00:00:00Z');

    const url = String(mock.mock.calls[0][0]);
    expect(url.startsWith('/api/ai/spend?')).toBe(true);
    const query = new URLSearchParams(url.slice(url.indexOf('?') + 1));
    expect(query.get('from')).toBe('2026-07-01T00:00:00Z');
    expect(query.get('to')).toBe('2026-07-31T00:00:00Z');
  });

  it('surfaces a 5xx as failed — never a fabricated zero-spend', async () => {
    stubFetch(() => Promise.resolve(response(500)));

    const result = await getAiSpend();

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.kind).toBe('failed');
  });

  it('reports a null cap unchanged — an inert governor has no cap, not a zero one', async () => {
    stubFetch(() => Promise.resolve(response(200, { ...SPEND, dailyBudgetUsd: null })));

    const result = await getAiSpend();

    expect(result.ok && result.data.dailyBudgetUsd).toBeNull();
  });
});
