import { afterEach, describe, expect, it, vi } from 'vitest';

import { type FlattenSchedule, getFlattenSchedule, remainingMs, soonestArmed } from './flatten';

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

const SCHEDULE: FlattenSchedule = {
  asOf: '2026-07-15T19:00:00Z',
  markets: [
    {
      instrument: 'ES',
      deadline: '14:30',
      deadlineUtc: '2026-07-15T19:30:00Z',
      enabled: true,
      source: 'BuiltInDefault',
    },
    {
      instrument: 'NQ',
      deadline: '14:30',
      deadlineUtc: '2026-07-15T19:30:00Z',
      enabled: true,
      source: 'BuiltInDefault',
    },
    {
      instrument: 'GC',
      deadline: '12:15',
      deadlineUtc: '2026-07-16T17:15:00Z',
      enabled: true,
      source: 'BuiltInDefault',
    },
  ],
};

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe('getFlattenSchedule', () => {
  it('reads the schedule from the server', async () => {
    stubFetch(() => Promise.resolve(response(200, SCHEDULE)));

    const result = await getFlattenSchedule();

    expect(result).toEqual({ ok: true, data: SCHEDULE });
  });

  it('requests the flatten schedule route', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(response(200, SCHEDULE)));

    await getFlattenSchedule();

    expect(String(fetchMock.mock.calls[0][0])).toContain('/flatten/schedule');
  });

  it('surfaces a failure rather than inventing a schedule', async () => {
    // A countdown is a safety display. If the read fails the strip must say so, not fall back to a
    // client-computed guess -- the whole reason this is a server read is that the client cannot compute it.
    stubFetch(() => Promise.resolve(response(503)));

    const result = await getFlattenSchedule();

    expect(result.ok).toBe(false);
  });
});

describe('soonestArmed', () => {
  it('picks the nearest deadline', () => {
    expect(soonestArmed(SCHEDULE)?.instrument).toBe('ES');
  });

  it('skips a market whose auto-flatten is disabled', () => {
    // R-13's deliberate override. An unarmed market has no deadline to count down to, so counting down to it
    // would show the operator protection that is switched off -- the single most misleading thing this strip
    // could do.
    const withEsDisabled: FlattenSchedule = {
      ...SCHEDULE,
      markets: [{ ...SCHEDULE.markets[0], enabled: false }, ...SCHEDULE.markets.slice(1)],
    };

    expect(soonestArmed(withEsDisabled)?.instrument).toBe('NQ');
  });

  it('returns null when nothing is armed', () => {
    const nothingArmed: FlattenSchedule = {
      ...SCHEDULE,
      markets: SCHEDULE.markets.map((market) => ({ ...market, enabled: false })),
    };

    expect(soonestArmed(nothingArmed)).toBeNull();
  });

  it('returns null when the server governs no markets', () => {
    expect(soonestArmed({ asOf: SCHEDULE.asOf, markets: [] })).toBeNull();
  });
});

describe('remainingMs', () => {
  it('measures from the server instant, not the browser clock', () => {
    // The point of `asOf`. 19:00Z -> 19:30Z is 30 minutes, however wrong the workstation clock is.
    expect(remainingMs(SCHEDULE.markets[0], SCHEDULE.asOf, 0)).toBe(30 * 60 * 1000);
  });

  it('subtracts how long the reading has been held', () => {
    // The strip ticks locally between fetches. Elapsed time is measured client-side, but only as a DURATION
    // (which is skew-free) -- never by re-reading the wall clock, which is not.
    expect(remainingMs(SCHEDULE.markets[0], SCHEDULE.asOf, 60_000)).toBe(29 * 60 * 1000);
  });

  it('clamps at zero rather than running negative', () => {
    // Past the deadline the honest display is 00:00 and a flatten in progress, not a negative number that reads
    // like time remaining.
    expect(remainingMs(SCHEDULE.markets[0], SCHEDULE.asOf, 60 * 60 * 1000)).toBe(0);
  });
});
