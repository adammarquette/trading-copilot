import { cleanup, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import type { DailyRealizedPnL } from '../api/journal';
import { hexToRgb } from '../testing/color';
import { renderWithProviders } from '../testing/render';
import { colorTokens } from '../theme/tokens';
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

  // Tone is what actually gets PAINTED, read independently of `toneOf` (gh#1115 — `toneOf` already has
  // its own unit tests; a test that re-derives its expectation from `toneOf` cannot fail on a wiring
  // defect such as a tile's tone being hardcoded rather than following its value). `mode` is forced
  // explicitly so the assertion never depends on whatever mode a prior test left in `localStorage` — see
  // `src/testing/color.ts` for the pattern.
  describe('tone — read where it is rendered, not from toneOf', () => {
    const ALL_RED: readonly DailyRealizedPnL[] = [
      { date: '2026-09-01', realizedPnL: -100, tradeCount: 1 },
      { date: '2026-09-02', realizedPnL: -400, tradeCount: 1 },
      { date: '2026-09-03', realizedPnL: -50, tradeCount: 1 },
    ];

    const ALL_GREEN: readonly DailyRealizedPnL[] = [
      { date: '2026-09-01', realizedPnL: 100, tradeCount: 1 },
      { date: '2026-09-02', realizedPnL: 400, tradeCount: 1 },
      { date: '2026-09-03', realizedPnL: 50, tradeCount: 1 },
    ];

    it("paints an all-red month's net and best day in the loss colour, never the profit one", () => {
      // The counter-intuitive case: the least-bad day of an all-red month is still a loss. Re-hardcoding
      // `tone="positive"` on the best tile (the pre-#1114 defect) would leave this red.
      renderWithProviders(<MonthSummary days={ALL_RED} />, { mode: 'dark' });

      const net = getComputedStyle(screen.getByTestId('journal-stat-net')).color;
      const best = getComputedStyle(screen.getByTestId('journal-stat-best')).color;
      const short = hexToRgb(colorTokens.dark.trading.short);
      const long = hexToRgb(colorTokens.dark.trading.long);

      expect(net).toBe(short);
      expect(net).not.toBe(long);
      expect(best).toBe(short);
      expect(best).not.toBe(long);
    });

    it("paints an all-green month's net and worst day in the profit colour, never the loss one", () => {
      // The mirror case: the least-good day of an all-green month is still a gain. Re-hardcoding
      // `tone="negative"` on the worst tile (the pre-#1114 defect) would leave this red.
      renderWithProviders(<MonthSummary days={ALL_GREEN} />, { mode: 'dark' });

      const net = getComputedStyle(screen.getByTestId('journal-stat-net')).color;
      const worst = getComputedStyle(screen.getByTestId('journal-stat-worst')).color;
      const short = hexToRgb(colorTokens.dark.trading.short);
      const long = hexToRgb(colorTokens.dark.trading.long);

      expect(net).toBe(long);
      expect(net).not.toBe(short);
      expect(worst).toBe(long);
      expect(worst).not.toBe(short);
    });
  });
});
