import { afterEach, describe, expect, it, vi } from 'vitest';

import { type ProtectionState, getProtectionState, isProtectionEvent } from './protection';

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

const DEGRADED: ProtectionState = {
  degraded: true,
  orphanedStops: 2,
  nativeSafetyStopRemainsFloor: true,
};

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe('getProtectionState', () => {
  it('reads the current state from the server', async () => {
    stubFetch(() => Promise.resolve(response(200, DEGRADED)));

    expect(await getProtectionState()).toEqual({ ok: true, data: DEGRADED });
  });

  it('requests the protection route', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(200, DEGRADED)));

    await getProtectionState();

    expect(String(fetchMock.mock.calls[0][0])).toContain('/protection');
  });

  it('surfaces a failure rather than reporting protected', async () => {
    // The dangerous direction. A failed read must not resolve to "not degraded" -- that renders the strip as
    // protected while the operator's tighter stop may be orphaned.
    stubFetch(() => Promise.resolve(response(503)));

    expect((await getProtectionState()).ok).toBe(false);
  });
});

describe('isProtectionEvent', () => {
  it('recognises the orphan transition', () => {
    expect(isProtectionEvent('protection.orphaned')).toBe(true);
  });

  it('recognises the restore transition', () => {
    // Both directions matter: restore is what CLEARS the alert, and treating it as unrelated would leave a
    // resolved degradation on screen permanently -- an alert an operator learns to ignore.
    expect(isProtectionEvent('protection.restored')).toBe(true);
  });

  it('ignores unrelated safety-strip traffic', () => {
    // The strip's other signals ride the same broadcast channel. Re-reading protection on a market quote would
    // hammer the endpoint at tick rate.
    expect(isProtectionEvent('killswitch.engaged')).toBe(false);
    expect(isProtectionEvent('flatten.warning')).toBe(false);
    expect(isProtectionEvent('market.quote')).toBe(false);
    expect(isProtectionEvent('')).toBe(false);
  });

  it('does not match on a prefix alone', () => {
    // Guards against a `startsWith('protection')` shortcut quietly matching a future unrelated type.
    expect(isProtectionEvent('protection')).toBe(false);
    expect(isProtectionEvent('protection.something-else')).toBe(false);
  });
});
