import { cleanup, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import type { DailyRealizedPnL } from '../api/journal';
import { renderWithProviders } from '../testing/render';
import { MonthSummary } from './MonthSummary';

const DAYS: readonly DailyRealizedPnL[] = [
  { date: '2026-09-01', realizedPnL: 1250, tradeCount: 3 },
  { date: '2026-09-02', realizedPnL: -400, tradeCount: 2 },
  { date: '2026-09-03', realizedPnL: 0, tradeCount: 1 },
];

afterEach(cleanup);

describe('MonthSummary', () => {
  it('shows the month net, the green/red split, the extremes and the average', () => {
    renderWithProviders(<MonthSummary days={DAYS} />);

    expect(screen.getByTestId('journal-stat-net').textContent).toBe('+$850.00');
    expect(screen.getByTestId('journal-stat-days').textContent).toBe('1 / 1');
    expect(screen.getByTestId('journal-stat-best').textContent).toBe('+$1,250.00');
    expect(screen.getByTestId('journal-stat-worst').textContent).toBe('-$400.00');
    expect(screen.getByTestId('journal-stat-average').textContent).toBe('+$283.33');
  });

  it('reports an untraded month as untraded, never as a breakeven one', () => {
    // "$0.00 net, $0.00 average" claims the operator traded to flat. Nothing was traded at all.
    renderWithProviders(<MonthSummary days={[]} />);

    expect(screen.getByTestId('journal-stat-average').textContent).toBe('—');
    expect(screen.getByTestId('journal-stat-best').textContent).toBe('—');
    expect(screen.getByTestId('journal-stat-worst').textContent).toBe('—');
  });

  it('draws the equity curve with one point per traded day', () => {
    renderWithProviders(<MonthSummary days={DAYS} />);

    const curve = screen.getByTestId('journal-equity-curve');
    expect(curve.querySelectorAll('circle')).toHaveLength(3);
  });

  it('labels the curve with where the month ended, so it is not a picture alone', () => {
    renderWithProviders(<MonthSummary days={DAYS} />);

    expect(
      screen.getByRole('img', { name: 'Cumulative realized P&L — the month ends at +$850.00' }),
    ).toBeTruthy();
  });

  it('says there is no curve rather than drawing an empty axis', () => {
    renderWithProviders(<MonthSummary days={[]} />);

    expect(screen.queryByTestId('journal-equity-curve')).toBeNull();
    expect(screen.getByText('Nothing realized this month yet.')).toBeTruthy();
  });
});
