import { cleanup, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import type { DailyHeadroom } from '../api/risk';
import { renderWithProviders } from '../testing/render';
import { HeadroomPanel } from './HeadroomPanel';

const BASE: DailyHeadroom = {
  underGovernor: 450,
  underDailyLossLimit: 800,
  governorSpent: false,
  dailyLossLimitSpent: false,
  dayLoss: 150,
  dayRealizedProfit: 0,
  dailyProfitTarget: 900,
  dailyTargetReached: false,
};

afterEach(cleanup);

describe('HeadroomPanel', () => {
  it('shows the room left under the personal governor', () => {
    renderWithProviders(<HeadroomPanel headroom={BASE} />);

    expect(screen.getByText('Under your daily governor')).toBeTruthy();
    expect(screen.getByText('$450.00')).toBeTruthy();
  });

  it('shows the firm daily limit row only when a firm limit exists', () => {
    const { rerender } = renderWithProviders(<HeadroomPanel headroom={BASE} />);
    expect(screen.getByText('Under the firm daily limit')).toBeTruthy();

    // rerender replaces the tree in place — no cleanup, which would unmount what rerender needs.
    rerender(<HeadroomPanel headroom={{ ...BASE, underDailyLossLimit: null }} />);
    expect(screen.queryByText('Under the firm daily limit')).toBeNull();
  });

  it('marks the governor spent when it is fully used', () => {
    renderWithProviders(<HeadroomPanel headroom={{ ...BASE, governorSpent: true }} />);

    expect(screen.getByText('spent')).toBeTruthy();
  });

  it('marks the daily target reached', () => {
    renderWithProviders(<HeadroomPanel headroom={{ ...BASE, dailyTargetReached: true }} />);

    expect(screen.getByText('reached')).toBeTruthy();
  });

  it('hides the daily-target row when the account has no target', () => {
    renderWithProviders(<HeadroomPanel headroom={{ ...BASE, dailyProfitTarget: null }} />);

    expect(screen.queryByText('Daily profit target')).toBeNull();
  });
});
