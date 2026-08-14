import { afterEach, describe, expect, it, vi } from 'vitest';

import { clearNewsFeedback, getNewsFeed, setNewsFeedback, SoftSignalKind } from './news';

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

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function item(overrides: Record<string, unknown> = {}) {
  return {
    dedupKey: 'k1',
    title: 'ES breaks out',
    url: 'https://example.test/es',
    publishedAt: '2026-08-14T00:00:00Z',
    instruments: ['ES'],
    topics: ['breakout'],
    sources: ['finnhub'],
    baseRelevance: 1,
    multiplier: 1.2,
    salience: 1.2,
    whyWeighted: 'Weighted up because you starred similar items on ES.',
    reasons: [{ dimension: 'Instrument', value: 'ES', contribution: 0.2 }],
    feedback: null,
    ...overrides,
  };
}

describe('getNewsFeed', () => {
  it('unwraps the items envelope from the personalized feed route', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(200, { items: [item()] })));

    const result = await getNewsFeed();

    expect(String(fetchMock.mock.calls[0][0])).toContain('/api/news');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('GET');
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.data).toHaveLength(1);
      expect(result.data[0].dedupKey).toBe('k1');
      expect(result.data[0].feedback).toBeNull();
    }
  });

  it('carries an optional limit as a query param', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(200, { items: [] })));

    await getNewsFeed(10);

    expect(String(fetchMock.mock.calls[0][0])).toContain('/api/news?limit=10');
  });

  it('surfaces a failed read rather than an empty feed', async () => {
    stubFetch(() => Promise.resolve(response(503)));

    expect((await getNewsFeed()).ok).toBe(false);
  });
});

describe('setNewsFeedback', () => {
  it('puts the dedup key and kind to the feedback route (the key rides in the body, not the path)', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(204)));

    await setNewsFeedback('k1', SoftSignalKind.Star);

    expect(String(fetchMock.mock.calls[0][0])).toContain('/api/news/feedback');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('PUT');
    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body));
    expect(body).toEqual({ dedupKey: 'k1', kind: 1 }); // Star travels as its integer
  });

  it('surfaces a refusal (an unknown kind, a phantom key) rather than reporting success', async () => {
    stubFetch(() => Promise.resolve(response(400, { error: 'Kind must be Star or Mute.' })));

    expect((await setNewsFeedback('k1', SoftSignalKind.Mute)).ok).toBe(false);
  });
});

describe('clearNewsFeedback', () => {
  it('deletes by the dedup key as a query param (a URL is not path-safe)', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(204)));

    await clearNewsFeedback('a/b?c');

    expect(String(fetchMock.mock.calls[0][0])).toContain('/api/news/feedback?dedupKey=a%2Fb%3Fc');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('DELETE');
  });
});
