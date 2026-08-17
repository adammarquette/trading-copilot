import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  acceptInvite,
  request,
  requestJson,
  setOnUnauthenticated,
  signIn,
  signOut,
} from './client';
import { readToken, storeToken } from './token';

/** A stand-in for the parts of Response the client reads: `ok`, `status`, and a text body. */
function response(status: number, body?: unknown): Response {
  return rawResponse(status, body === undefined ? '' : JSON.stringify(body));
}

/**
 * The same stand-in, but with the body passed through **verbatim** rather than serialized — the only way to
 * express a response whose body is not JSON at all, which is exactly the gh#951 case (a proxy or misconfigured
 * host answering 200 with an HTML error page).
 */
function rawResponse(status: number, text: string): Response {
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

  it('maps a 2xx carrying a non-JSON body to failed — never a rejected promise (gh#951)', async () => {
    // A proxy or misconfigured host answering 200 with an HTML error page. Before gh#951 the 2xx body read sat
    // OUTSIDE request()'s try/catch, so JSON.parse threw and the promise rejected. Callers that do
    // `void getX().then(...)` with no `.catch` — the shape every read surface uses — then sat on their
    // LoadingState forever, with no error and no retry: the one outcome these surfaces exist to avoid.
    stubFetch(() =>
      Promise.resolve(rawResponse(200, '<!doctype html><title>502 Bad Gateway</title>')),
    );

    const result = await request('GET', '/settings/ai-spend');

    expect(result).toEqual({
      ok: false,
      kind: 'failed',
      status: 200,
      error: 'The response body could not be read.',
    });
  });

  it('maps a 2xx carrying a truncated JSON body to failed, not a rejection (gh#951)', async () => {
    // The same defect reached the other way: a body that starts as valid JSON and stops mid-object. Kept
    // separate from the HTML case because a guard that only sniffed for a leading '<' would pass that one.
    stubFetch(() => Promise.resolve(rawResponse(200, '{"value": 4')));

    const result = await request<{ value: number }>('GET', '/anything');

    expect(result.ok).toBe(false);
  });

  it('requestJson maps a 2xx with an EMPTY body to failed — the read has no payload (gh#963)', async () => {
    // The third face of the gh#951/gh#954 class. An empty body does not fail to parse — it is the legitimate 204
    // path — so `request` hands back `undefined` and every caller that dereferences `result.data.<prop>` throws.
    // On a `void …then(…)` path (which is every read surface) that is an unhandled rejection: the panel never
    // leaves its loading state. A read that was supposed to carry a payload and carried none is a FAILED read.
    stubFetch(() => Promise.resolve(rawResponse(200, '')));

    const result = await requestJson<{ positions: unknown[] }>('GET', '/accounts/1/positions');

    expect(result).toEqual({
      ok: false,
      kind: 'failed',
      error: 'The response carried no data.',
    });
  });

  it('requestJson passes a present body through unchanged', async () => {
    // The control. Without it the case above passes against a `requestJson` that fails EVERY read.
    stubFetch(() => Promise.resolve(response(200, { positions: [1, 2] })));

    const result = await requestJson<{ positions: number[] }>('GET', '/accounts/1/positions');

    expect(result).toEqual({ ok: true, data: { positions: [1, 2] } });
  });

  it('requestJson keeps a refusal a refusal — it does not flatten 4xx answers into failures', async () => {
    // The `refused` vs `failed` split (R-11) is the client's reason for existing; the new wrapper must not blunt
    // it. A gate answer still arrives as an answer, with its reason and binding layer intact.
    stubFetch(() =>
      Promise.resolve(
        response(409, { error: 'The daily loss limit is spent.', layer: 'DailyLossLimit' }),
      ),
    );

    const result = await requestJson<unknown>('POST', '/accounts/1/orders', {});

    expect(result).toEqual({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'The daily loss limit is spent.',
      layer: 'DailyLossLimit',
    });
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

  it('maps a 5xx that DOES carry an { error } body to failed, not refused — a 5xx is not a gate answer', async () => {
    // An unhandled exception / ASP.NET ProblemDetails / proxy response can return 500 with `{ error }`.
    // Classifying that as `refused` (R-11) would render a transient server fault as an authoritative "the gate
    // said no" — never retried — for a request the gate may never have evaluated. It is a `failed`.
    stubFetch(() => Promise.resolve(response(500, { error: 'An unexpected error occurred.' })));

    const result = await request('POST', '/accounts/1/orders', {});

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.kind).toBe('failed');
      expect(result.status).toBe(500);
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

  // gh#954 — a 2xx whose body carries no usable token. gh#951 guarded a body that FAILS TO PARSE; an empty body
  // does not fail to parse, it is the legitimate `undefined` (204) path, so it flowed straight through to
  // `storeToken(result.data.token)` and threw `TypeError: Cannot read properties of undefined`. That rejection
  // escaped `SignInPage.onSubmit`'s un-caught await, so `setSubmitting(false)` never ran and the operator was
  // left on a dead submit button with no error — on the one surface that gates every other surface.
  it('treats a 2xx with an EMPTY body as a failed sign-in, never a throw (gh#954)', async () => {
    stubFetch(() => Promise.resolve(rawResponse(200, '')));

    const result = await signIn({ email: 'op@local', password: 'pw' });

    expect(result.ok).toBe(false);
    expect(readToken()).toBeNull();
  });

  it('treats a 2xx whose body has no token as a failed sign-in (gh#954)', async () => {
    // Parses fine, carries nothing usable — a proxy substituting its own JSON, or a contract drift.
    stubFetch(() => Promise.resolve(response(200, { somethingElse: true })));

    const result = await signIn({ email: 'op@local', password: 'pw' });

    expect(result.ok).toBe(false);
    expect(readToken()).toBeNull();
  });

  it('treats a 2xx whose token is not a string as a failed sign-in (gh#954)', async () => {
    // The half-guard this case exists to stop: a `token in data` check passes here and stores `[object Object]`,
    // which is a *stored session that cannot work* — worse than a clean refusal, because the app looks signed in.
    stubFetch(() => Promise.resolve(response(200, { token: { nested: 'no' } })));

    const result = await signIn({ email: 'op@local', password: 'pw' });

    expect(result.ok).toBe(false);
    expect(readToken()).toBeNull();
  });

  it('treats a 2xx with an empty body as a failed accept-invite (gh#954)', async () => {
    // Same seam, same dereference — acceptInvite is signIn's twin and would otherwise keep the defect alive.
    stubFetch(() => Promise.resolve(rawResponse(200, '')));

    const result = await acceptInvite({ token: 'invite', displayName: 'Op', password: 'pw' });

    expect(result.ok).toBe(false);
    expect(readToken()).toBeNull();
  });

  it('signOut drops the token', () => {
    storeToken('jwt');

    signOut();

    expect(readToken()).toBeNull();
  });
});
