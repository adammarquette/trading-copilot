import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  KillSwitchMode,
  type KillSwitchState,
  disengageKillSwitch,
  engageKillSwitch,
  getKillSwitch,
  isKillSwitchEvent,
} from './killSwitch';

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

const DISENGAGED: KillSwitchState = {
  engaged: false,
  mode: 'FlattenAll',
  engagedAt: null,
  reason: null,
};

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe('getKillSwitch', () => {
  it('reads the current state', async () => {
    stubFetch(() => Promise.resolve(response(200, DISENGAGED)));

    const result = await getKillSwitch();

    expect(result).toEqual({ ok: true, data: DISENGAGED });
  });

  it('reads an engaged state back after a restart', async () => {
    // The property worth surfacing rather than hiding (gh#189): the state is rehydrated at startup, so nothing
    // silently re-enables trading. The client must therefore treat the server as the source of truth on load
    // rather than assuming a fresh session starts disengaged.
    const engaged: KillSwitchState = {
      engaged: true,
      mode: 'HaltOnly',
      engagedAt: '2026-08-10T13:00:00Z',
      reason: 'stepping away',
    };
    stubFetch(() => Promise.resolve(response(200, engaged)));

    const result = await getKillSwitch();

    expect(result).toEqual({ ok: true, data: engaged });
  });
});

describe('engageKillSwitch', () => {
  it('sends the hold-to-confirm gesture', async () => {
    // The server answers 422 without it (R-11): a panic action must not fire from a stray request. The client
    // sends it only from the deliberate gesture, never as a constant baked into the request builder.
    const fetchMock = stubFetch(() =>
      Promise.resolve(
        response(200, { mode: 'FlattenAll', cancelledOrders: 0, flattenedPositions: 0 }),
      ),
    );

    await engageKillSwitch(KillSwitchMode.FlattenAll, 'stepping away');

    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body)) as Record<string, unknown>;
    expect(body.confirmed).toBe(true);
  });

  it('sends the mode as its integer, which is what the server binds', async () => {
    // Asymmetric on purpose and easy to get wrong: there is no JsonStringEnumConverter server-side, so the
    // REQUEST carries the integer while the RESPONSE returns the name. Sending "HaltOnly" here binds nothing and
    // silently falls back to FlattenAll -- flattening positions the operator asked to leave on their stops.
    const fetchMock = stubFetch(() =>
      Promise.resolve(
        response(200, { mode: 'HaltOnly', cancelledOrders: 2, flattenedPositions: 0 }),
      ),
    );

    await engageKillSwitch(KillSwitchMode.HaltOnly, null);

    const body = JSON.parse(String(fetchMock.mock.calls[0][1]?.body)) as Record<string, unknown>;
    expect(body.mode).toBe(1);
  });

  it('reports what engaging actually did', async () => {
    stubFetch(() =>
      Promise.resolve(
        response(200, { mode: 'FlattenAll', cancelledOrders: 3, flattenedPositions: 2 }),
      ),
    );

    const result = await engageKillSwitch(KillSwitchMode.FlattenAll, null);

    expect(result).toEqual({
      ok: true,
      data: { mode: 'FlattenAll', cancelledOrders: 3, flattenedPositions: 2 },
    });
  });

  it('surfaces a refusal rather than reporting success', async () => {
    stubFetch(() => Promise.resolve(response(422, { error: 'confirmation required' })));

    const result = await engageKillSwitch(KillSwitchMode.FlattenAll, null);

    expect(result.ok).toBe(false);
  });
});

describe('disengageKillSwitch', () => {
  it('posts to the disengage route', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(200, { engaged: false })));

    await disengageKillSwitch();

    expect(String(fetchMock.mock.calls[0][0])).toContain('/kill-switch/disengage');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('POST');
  });
});

describe('isKillSwitchEvent', () => {
  it('matches every broadcast that changes the kill-switch state', () => {
    // The strip's second window must re-read on each: an operator's engage / disengage in one window, and the
    // watchdog's escalation, are exactly what a stale pop-out would otherwise miss (gh#985).
    expect(isKillSwitchEvent('killswitch.engaged')).toBe(true);
    expect(isKillSwitchEvent('killswitch.disengaged')).toBe(true);
    expect(isKillSwitchEvent('killswitch.escalated')).toBe(true);
  });

  it('ignores the other traffic on the shared safety-strip channel', () => {
    // An exact-match set, not a prefix: protection and flatten events (and market quotes) ride the same channel,
    // and a re-read on each would be wasteful churn — and a future killswitch.* type must be adopted deliberately.
    expect(isKillSwitchEvent('protection.orphaned')).toBe(false);
    expect(isKillSwitchEvent('flatten.executed')).toBe(false);
    expect(isKillSwitchEvent('market.quote')).toBe(false);
    expect(isKillSwitchEvent('killswitch.something-new')).toBe(false);
  });
});
