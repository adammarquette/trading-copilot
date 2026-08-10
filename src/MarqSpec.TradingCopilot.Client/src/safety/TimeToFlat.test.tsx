import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { FlattenSchedule } from '../api/flatten';
import { getFlattenSchedule } from '../api/flatten';
import { TimeToFlat } from './TimeToFlat';

vi.mock('../api/flatten', async (importOriginal) => ({
  // The selectors stay REAL -- they are the tested arithmetic, and stubbing them here would let the countdown
  // pass this suite while computing the wrong number in the browser.
  ...(await importOriginal<typeof import('../api/flatten')>()),
  getFlattenSchedule: vi.fn(),
}));

const read = vi.mocked(getFlattenSchedule);

/** 19:00Z server time, ES armed 30 minutes out, GC armed a day out. */
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
      instrument: 'GC',
      deadline: '12:15',
      deadlineUtc: '2026-07-16T17:15:00Z',
      enabled: true,
      source: 'BuiltInDefault',
    },
  ],
};

function countdown() {
  return screen.getByTestId('time-to-flat');
}

function shownText() {
  return countdown().textContent ?? '';
}

function showsAClock() {
  return /\d\d:\d\d/.test(shownText());
}

async function renderCountdown() {
  const view = render(<TimeToFlat />);
  await act(async () => {});
  return view;
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  read.mockResolvedValue({ ok: true, data: SCHEDULE });
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.clearAllMocks();
});

describe('TimeToFlat', () => {
  it('counts down to the soonest armed deadline', async () => {
    await renderCountdown();

    expect(shownText()).toContain('30:00');
  });

  it('names the market it is counting down to', async () => {
    // Four markets have four different deadlines. A bare clock would leave the operator guessing which position
    // it governs -- and the answer changes during the day as each deadline passes.
    await renderCountdown();

    expect(shownText()).toContain('ES');
  });

  it('ticks down as time passes', async () => {
    await renderCountdown();

    await act(async () => {
      vi.advanceTimersByTime(65_000);
    });

    expect(shownText()).toContain('28:55');
  });

  it('states the deadline in market time so the operator can check it', async () => {
    await renderCountdown();

    expect(countdown().getAttribute('aria-description')).toContain('14:30');
  });

  it('says so explicitly when no market is armed, and shows no time', async () => {
    // R-13's deliberate override. Rendering "--:--" beside an unlabelled clock face would read as "loading";
    // the operator must be told auto-flatten will not act, because on a live account nothing else will.
    read.mockResolvedValue({
      ok: true,
      data: {
        ...SCHEDULE,
        markets: SCHEDULE.markets.map((market) => ({ ...market, enabled: false })),
      },
    });

    await renderCountdown();

    expect(shownText().toLowerCase()).toContain('not armed');
    expect(showsAClock()).toBe(false);
  });

  it('says the schedule is unavailable rather than inventing a countdown', async () => {
    // The failure mode this component exists to avoid. A fabricated or stale number here is worse than an
    // obvious gap: it is a safety display, and it would be believed.
    read.mockResolvedValue({ ok: false, kind: 'failed', status: 503, error: 'unavailable' });

    await renderCountdown();

    expect(shownText().toLowerCase()).toContain('unavailable');
    expect(showsAClock()).toBe(false);
  });

  it('never runs negative once the deadline passes', async () => {
    // Deliberately inside one refresh window: a deadline further out than REFRESH_MS could never show the clamp
    // here, because the re-read would re-sync first. Past the deadline the honest display is 00:00 and a flatten
    // under way -- a negative number reads like time remaining, which is the opposite of the truth.
    read.mockResolvedValue({
      ok: true,
      data: {
        asOf: '2026-07-15T19:00:00Z',
        markets: [{ ...SCHEDULE.markets[0], deadlineUtc: '2026-07-15T19:01:00Z' }],
      },
    });

    await renderCountdown();

    await act(async () => {
      vi.advanceTimersByTime(2 * 60 * 1000);
    });

    expect(shownText()).not.toContain('-');
    expect(shownText()).toContain('00:00');
  });

  it('adopts the next session the server hands back on refresh', async () => {
    // The other half of not sitting at zero: the roll is the SERVER's answer (it re-resolves the wall-clock
    // deadline against the next date, which is an hour different across a daylight-saving change). The client
    // adopts it rather than deriving one.
    await renderCountdown();
    expect(shownText()).toContain('30:00');

    read.mockResolvedValue({
      ok: true,
      data: {
        asOf: '2026-07-15T19:31:00Z',
        markets: [{ ...SCHEDULE.markets[0], deadlineUtc: '2026-07-16T19:30:00Z' }],
      },
    });

    await act(async () => {
      vi.advanceTimersByTime(5 * 60 * 1000);
    });

    expect(shownText()).toContain('23:59:00');
  });

  it('shows hours when the next deadline is more than an hour away', async () => {
    read.mockResolvedValue({ ok: true, data: { ...SCHEDULE, markets: [SCHEDULE.markets[1]] } });

    await renderCountdown();

    expect(shownText()).toContain('22:15:00');
  });

  it('re-reads the schedule so it rolls to the next session on its own', async () => {
    // Without this the strip would sit at 00:00 for the rest of the day after the first deadline passes. The
    // roll to the next occurrence is the server's answer, so the client asks again rather than deriving it.
    await renderCountdown();
    expect(read).toHaveBeenCalledTimes(1);

    await act(async () => {
      vi.advanceTimersByTime(5 * 60 * 1000);
    });

    expect(read.mock.calls.length).toBeGreaterThan(1);
  });
});
