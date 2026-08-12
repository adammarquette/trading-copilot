import { cleanup, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { useAccounts } from '../accounts/AccountProvider';
import { type Account, TradingMode } from '../api/accounts';
import type { Destination } from '../navigation/destinations';
import { renderWithProviders } from '../testing/render';
import { SettingsSurface } from './SettingsSurface';

vi.mock('../accounts/AccountProvider', () => ({ useAccounts: vi.fn() }));

// Stub the data-fetching risk section: this suite is about the surface's account gating and the shell contract,
// not the risk load (covered in RiskSettings.test).
vi.mock('./RiskSettings', () => ({
  RiskSettings: ({ accountId }: { accountId: string }) => (
    <div data-testid="risk-settings">{accountId}</div>
  ),
}));

// Stub the onboarding walk for the same reason: the surface's job is to mount it in the empty branch and wire
// finishing to the roster reload — the walk itself is covered in FirmOnboarding.test.
vi.mock('../accounts/FirmOnboarding', () => ({
  FirmOnboarding: ({ onComplete }: { onComplete: () => void }) => (
    <button type="button" data-testid="firm-onboarding" onClick={onComplete}>
      onboarding
    </button>
  ),
}));

const accountsMock = vi.mocked(useAccounts);

const destination = {
  id: 'settings',
  label: 'Settings',
  path: '/settings',
} as unknown as Destination;

function account(id: string): Account {
  return {
    id,
    venueAccountKey: `V-${id}`,
    name: `Account ${id}`,
    stage: 1,
    stageOverride: null,
    mode: TradingMode.Practice,
    canTrade: true,
    isVisible: true,
    balance: 1000,
    tradeableHere: true,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(cleanup);

describe('SettingsSurface', () => {
  it('tags the surface for the shell and mounts the risk section when an account is active', () => {
    accountsMock.mockReturnValue({
      status: 'ready',
      accounts: [account('a1')],
      activeAccount: account('a1'),
      setActiveAccount: vi.fn(),
      reload: vi.fn(),
    });

    renderWithProviders(<SettingsSurface destination={destination} />);

    expect(screen.getByTestId('surface').dataset.surface).toBe('settings');
    expect(screen.getByTestId('risk-settings')).toBeTruthy();
  });

  it('scopes the risk section to the active account', () => {
    accountsMock.mockReturnValue({
      status: 'ready',
      accounts: [account('a9')],
      activeAccount: account('a9'),
      setActiveAccount: vi.fn(),
      reload: vi.fn(),
    });

    renderWithProviders(<SettingsSurface destination={destination} />);

    expect(screen.getByTestId('risk-settings').textContent).toBe('a9');
  });

  it('shows loading while the account context resolves', () => {
    accountsMock.mockReturnValue({ status: 'loading' });

    renderWithProviders(<SettingsSurface destination={destination} />);

    expect(screen.getByTestId('surface').dataset.surface).toBe('settings');
    expect(screen.queryByTestId('risk-settings')).toBeNull();
  });

  it('surfaces an account-context error with a retry', () => {
    const reload = vi.fn();
    accountsMock.mockReturnValue({ status: 'error', message: 'no context', reload });

    renderWithProviders(<SettingsSurface destination={destination} />);

    expect(screen.getByText('no context')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });

  it('starts a fresh operator on the onboarding walk when there are no accounts', () => {
    const reload = vi.fn();
    accountsMock.mockReturnValue({ status: 'empty', reload });

    renderWithProviders(<SettingsSurface destination={destination} />);

    // No account to declare risk against yet, so the empty branch is the firm → … → discover walk, and
    // finishing it reloads the roster so the surface re-scopes into the account-scoped view.
    const onboarding = screen.getByTestId('firm-onboarding');
    expect(onboarding).toBeTruthy();
    expect(screen.queryByTestId('risk-settings')).toBeNull();

    onboarding.click();
    expect(reload).toHaveBeenCalledTimes(1);
  });
});
