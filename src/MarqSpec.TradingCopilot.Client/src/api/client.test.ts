import { afterEach, describe, expect, it, vi } from 'vitest';

import { request, setOnUnauthenticated, signIn, signOut } from './client';
import { readToken, storeToken } from './token';

/** A stand-in for the parts of Response the client reads: `ok`, `status`, and a text body. */
function response(status: number, body?: unknown): Response {
  const text = body === undefined ? '' : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    text: () => Promise.resolve(text),
  } as unknown as Response;
}

function stubFetch(impl: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>) {
  const fetchMock = vi.fn(impl);
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function headerOf(init: RequestInit | undefined, name: string): string | undefined {
  return (init?.headers as Record<string, string> | undefined)?.[name];
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
  setOnUnauthenticated(() => {});
});

describe('request — the one JWT-attach path', () => {
  it('attaches the bearer token on a protected path', async () => {
    storeToken('jwt-123');
    const fetchMock = stubFetch(() => Promise.resolve(response(200, { ok: true })));

    await request('GET', '/accounts/abc/positions');

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/accounts/abc/positions'); // relative — same origin as the served bundle
    expect(headerOf(init, 'Authorization')).toBe('Bearer jwt-123');
  });

  it('does NOT attach the bearer on an anonymous path, even when a token is present', async () => {
    storeToken('jwt-123');
    const fetchMock = stubFetch(() => Promise.resolve(response(200, { token: 'x' })));

    await request('POST', '/auth/login', { email: 'a', password: 'b' });

    expect(headerOf(fetchMock.mock.calls[0][1], 'Authorization')).toBeUndefined();
  });

  it('returns ok with the parsed body on success', async () => {
    stubFetch(() => Promise.resolve(response(200, { value: 42 })));

    const result = await request<{ value: number }>('GET', '/anything');

    expect(result).toEqual({ ok: true, data: { value: 42 } });
  });

  it('returns ok with undefined data on a 204', async () => {
    stubFetch(() => Promise.resolve(response(204)));

    const result = await request('DELETE', '/orders/1');

    expect(result).toEqual({ ok: true, data: undefined });
  });

  it('on a protected 401 clears the token, notifies, and returns failed — never retries', async () => {
    storeToken('jwt');
    const onUnauthenticated = vi.fn();
    setOnUnauthenticated(onUnauthenticated);
    const fetchMock = stubFetch(() => Promise.resolve(response(401)));

    const result = await request('GET', '/accounts/1/risk');

    expect(readToken()).toBeNull();
    expect(onUnauthenticated).toHaveBeenCalledOnce();
    expect(fetchMock).toHaveBeenCalledOnce(); // no retry
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.kind).toBe('failed');
      expect(result.status).toBe(401);
    }
  });

  it('on an anonymous 401 (bad credentials) does not clear the token or redirect', async () => {
    storeToken('stale-jwt');
    const onUnauthenticated = vi.fn();
    setOnUnauthenticated(onUnauthenticated);
    stubFetch(() => Promise.resolve(response(401)));

    await request('POST', '/auth/login', { email: 'a', password: 'wrong' });

    expect(readToken()).toBe('stale-jwt'); // untouched
    expect(onUnauthenticated).not.toHaveBeenCalled();
  });

  it('maps a 4xx { error, layer } to a refusal — an answer, not a failure', async () => {
    stubFetch(() =>
      Promise.resolve(
        response(409, { error: 'The daily loss limit is spent.', layer: 'DailyLossLimit' }),
      ),
    );

    const result = await request('POST', '/accounts/1/orders', {});

    expect(result).toEqual({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'The daily loss limit is spent.',
      layer: 'DailyLossLimit',
    });
  });

  it('maps a 4xx { error } without a layer to a refusal with no layer', async () => {
    stubFetch(() =>
      Promise.resolve(response(422, { error: 'The kill switch requires confirmation.' })),
    );

    const result = await request('POST', '/kill-switch', {});

    expect(result).toEqual({
      ok: false,
      kind: 'refused',
      status: 422,
      reason: 'The kill switch requires confirmation.',
      layer: undefined,
    });
  });

  it('maps a 5xx without an actionable body to failed (retry is meaningful)', async () => {
    stubFetch(() => Promise.resolve(response(503)));

    const result = await request('GET', '/anything');

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.kind).toBe('failed');
      expect(result.status).toBe(503);
    }
  });

  it('maps a thrown fetch (offline / aborted) to failed', async () => {
    stubFetch(() => Promise.reject(new TypeError('Failed to fetch')));

    const result = await request('GET', '/anything');

    expect(result.ok).toBe(false);
    if (!result.ok && result.kind === 'failed') {
      expect(result.error).toContain('Failed to fetch');
    } else {
      expect.fail('expected a failed result');
    }
  });
});

describe('signIn / signOut', () => {
  it('stores the JWT on a successful sign-in', async () => {
    stubFetch(() => Promise.resolve(response(200, { token: 'jwt-xyz' })));

    const result = await signIn({ email: 'op@local', password: 'pw' });

    expect(result.ok).toBe(true);
    expect(readToken()).toBe('jwt-xyz');
  });

  it('stores no token when the credentials are rejected (401)', async () => {
    stubFetch(() => Promise.resolve(response(401)));

    const result = await signIn({ email: 'op@local', password: 'wrong' });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.status).toBe(401);
    }
    expect(readToken()).toBeNull();
  });

  it('signOut drops the token', () => {
    storeToken('jwt');

    signOut();

    expect(readToken()).toBeNull();
  });
});
