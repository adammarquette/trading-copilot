import { cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { DailyRealizedPnL } from '../api/journal';
import { renderWithProviders } from '../testing/render';
import { PnlCalendar } from './PnlCalendar';

const DAYS: readonly DailyRealizedPnL[] = [
  { date: '2026-09-01', realizedPnL: 1250, tradeCount: 3 },
  { date: '2026-09-02', realizedPnL: -400, tradeCount: 2 },
];

afterEach(cleanup);

function renderCalendar(overrides: Partial<Parameters<typeof PnlCalendar>[0]> = {}) {
  const onSelectDate = vi.fn();
  renderWithProviders(
    <PnlCalendar
      month="2026-09"
      days={DAYS}
      selectedDate="2026-09-01"
      onSelectDate={onSelectDate}
      {...overrides}
    />,
  );
  return onSelectDate;
}

describe('PnlCalendar', () => {
  it('names a traded day with its realized P&L and trade count, not just a colour', () => {
    // The magnitude shading is the glance; the accessible name is what a screen reader and a
    // colour-blind operator actually read. A calendar that encodes the whole answer in hue is unreadable
    // to both.
    renderCalendar();

    expect(screen.getByRole('button', { name: 'September 1 — +$1,250.00, 3 trades' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'September 2 — -$400.00, 2 trades' })).toBeTruthy();
  });

  it('says a quiet day was not traded rather than showing it as breakeven', () => {
    renderCalendar();

    expect(screen.getByRole('button', { name: 'September 10 — no trades' })).toBeTruthy();
  });

  it('marks the selected day as pressed', () => {
    renderCalendar();

    expect(
      screen
        .getByRole('button', { name: 'September 1 — +$1,250.00, 3 trades' })
        .getAttribute('aria-pressed'),
    ).toBe('true');
    expect(
      screen
        .getByRole('button', { name: 'September 2 — -$400.00, 2 trades' })
        .getAttribute('aria-pressed'),
    ).toBe('false');
  });

  it('drills into a day when it is picked', () => {
    const onSelectDate = renderCalendar();

    fireEvent.click(screen.getByRole('button', { name: 'September 2 — -$400.00, 2 trades' }));

    expect(onSelectDate).toHaveBeenCalledWith('2026-09-02');
  });

  it('lets the operator drill into a quiet day too — zero trades is an answer', () => {
    const onSelectDate = renderCalendar();

    fireEvent.click(screen.getByRole('button', { name: 'September 10 — no trades' }));

    expect(onSelectDate).toHaveBeenCalledWith('2026-09-10');
  });

  it('renders the month it was asked for, not the browser\u2019s today', () => {
    renderCalendar({ month: '2026-02', days: [], selectedDate: '2026-02-03' });

    expect(screen.getByText('February 2026')).toBeTruthy();
    // February 2026 has 28 days; a 29th button would mean the length came from somewhere else.
    expect(screen.queryByRole('button', { name: /^February 29/ })).toBeNull();
    expect(screen.getByRole('button', { name: 'February 28 — no trades' })).toBeTruthy();
  });
});
