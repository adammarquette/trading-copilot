import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { type DailyRealizedPnL, getDailyRealizedPnL } from '../api/journal';
import { renderWithProviders } from '../testing/render';
import { JournalMonth } from './JournalMonth';

vi.mock('../api/journal', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/journal')>()),
  getDailyRealizedPnL: vi.fn(),
}));

// The day drill-down owns its own read; this component's job is to point it at the picked day.
vi.mock('./DayDetail', () => ({
  DayDetail: ({ accountId, date }: { accountId: string; date: string }) => (
    <div data-testid="day-detail">{`${accountId}/${date}`}</div>
  ),
}));

const calendarMock = vi.mocked(getDailyRealizedPnL);

const DAYS: readonly DailyRealizedPnL[] = [
  { date: '2026-09-01', realizedPnL: 1250, tradeCount: 3 },
  { date: '2026-09-02', realizedPnL: -400, tradeCount: 2 },
];

beforeEach(() => {
  vi.clearAllMocks();
  calendarMock.mockResolvedValue({ ok: true, data: DAYS });
});

afterEach(cleanup);

function renderMonth(today = '2026-09-04') {
  return renderWithProviders(<JournalMonth accountId="a1" today={today} />);
}

/** A read whose settlement this test controls, so response ORDER can be inverted deliberately. */
function deferredRead() {
  let settle!: (value: Awaited<ReturnType<typeof getDailyRealizedPnL>>) => void;
  const promise = new Promise<Awaited<ReturnType<typeof getDailyRealizedPnL>>>((resolve) => {
    settle = resolve;
  });
  return { promise, settle };
}

describe('JournalMonth', () => {
  it('opens on the current Central month and drills into today by default (R-8)', async () => {
    renderMonth();

    await waitFor(() => {
      expect(calendarMock).toHaveBeenCalledWith('a1', '2026-09-01', '2026-09-30');
    });
    expect(screen.getByTestId('day-detail').textContent).toBe('a1/2026-09-04');
  });

  it('re-reads the calendar when the operator steps back a month', async () => {
    renderMonth();
    await screen.findByText('September 2026');

    fireEvent.click(screen.getByRole('button', { name: 'Previous month' }));

    await waitFor(() => {
      expect(calendarMock).toHaveBeenLastCalledWith('a1', '2026-08-01', '2026-08-31');
    });
    expect(screen.getByText('August 2026')).toBeTruthy();
  });

  it('moves the drill-down into the month it just opened, never leaving it on a day off the grid', async () => {
    renderMonth();
    await screen.findByText('September 2026');

    fireEvent.click(screen.getByRole('button', { name: 'Previous month' }));

    expect(await screen.findByText('a1/2026-08-01')).toBeTruthy();
  });

  it('comes back to today when it returns to the current month', async () => {
    renderMonth();
    await screen.findByText('September 2026');

    fireEvent.click(screen.getByRole('button', { name: 'Previous month' }));
    await screen.findByText('August 2026');
    fireEvent.click(screen.getByRole('button', { name: 'Next month' }));

    expect(await screen.findByText('a1/2026-09-04')).toBeTruthy();
  });

  it('will not page into a month that has not happened', async () => {
    // There is nothing to review in the future, and an empty calendar there reads as a data loss.
    renderMonth();
    await screen.findByText('September 2026');

    expect(screen.getByRole('button', { name: 'Next month' }).hasAttribute('disabled')).toBe(true);
  });

  it('drills into the day the operator picks on the calendar', async () => {
    renderMonth();
    await screen.findByText('September 2026');

    fireEvent.click(screen.getByRole('button', { name: 'September 2 \u2014 -$400.00, 2 trades' }));

    expect(screen.getByTestId('day-detail').textContent).toBe('a1/2026-09-02');
  });

  it('names the undeclared-account case rather than an empty month', async () => {
    calendarMock.mockResolvedValue({ ok: true, data: null });

    renderMonth();

    expect(await screen.findByText('This account has no journal')).toBeTruthy();
  });

  it('offers a retry when the calendar could not be read', async () => {
    calendarMock.mockResolvedValueOnce({ ok: false, kind: 'failed', error: 'network down' });

    renderMonth();

    expect(await screen.findByText('network down')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('September 2026')).toBeTruthy();
  });

  // `mounted` catches teardown, not supersession. The grid filters its rows to the month it was asked for
  // (`monthGrid`) while the stat strip takes `days` unfiltered (`monthStats` / `equityCurve`), so a superseded
  // read does not merely look stale -- it repaints the strip from a month the calendar is no longer drawing.
  it('never paints one month’s stat strip beside another month’s calendar', async () => {
    const august = deferredRead();
    const july = deferredRead();
    calendarMock.mockReset();
    calendarMock
      .mockResolvedValueOnce({ ok: true, data: [] })
      .mockReturnValueOnce(august.promise)
      .mockReturnValueOnce(july.promise);

    renderMonth();
    await screen.findByText('September 2026');

    // The month label only renders once a read has landed, so each step is keyed off the READ it issues --
    // the deferred months never reach 'loaded' until this test lets them.
    fireEvent.click(screen.getByRole('button', { name: 'Previous month' }));
    await waitFor(() => {
      expect(calendarMock).toHaveBeenLastCalledWith('a1', '2026-08-01', '2026-08-31');
    });
    fireEvent.click(screen.getByRole('button', { name: 'Previous month' }));
    await waitFor(() => {
      expect(calendarMock).toHaveBeenLastCalledWith('a1', '2026-07-01', '2026-07-31');
    });

    // July answers first and is the month on screen; the superseded August read lands after it.
    july.settle({ ok: true, data: [] });
    expect(await screen.findByText('July 2026')).toBeTruthy();
    expect(screen.getByTestId('journal-stat-net').textContent).toBe('—');

    // Inside `act` so the component's own `.then` and the render it schedules are both flushed before the
    // assertion -- settling the promise alone leaves the repaint pending and the check passes vacuously.
    await act(async () => {
      august.settle({ ok: true, data: DAYS });
      await august.promise;
    });

    expect(screen.getByText('July 2026')).toBeTruthy();
    expect(screen.getByTestId('journal-stat-net').textContent).toBe('—');
  });
});
