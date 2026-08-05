import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { type Account, TradingMode } from '../api/accounts';
import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import { useOptionalAccounts } from './AccountProvider';
import { AccountSwitcher } from './AccountSwitcher';

vi.mock('./AccountProvider', () => ({ useOptionalAccounts: vi.fn() }));
const useOptionalAccountsMock = vi.mocked(useOptionalAccounts);

function account(id: string, mode: TradingMode, isVisible = true): Account {
  return {
    id,
    venueAccountKey: `V-${id}`,
    name: `Account ${id}`,
    stage: 1,
    stageOverride: null,
    mode,
    canTrade: true,
    isVisible,
    balance: 1000,
    tradeableHere: mode !== TradingMode.Undeclared,
  };
}

function ready(accounts: Account[], activeAccount: Account, setActiveAccount = vi.fn()) {
  return { status: 'ready', accounts, activeAccount, setActiveAccount, reload: vi.fn() } as const;
}

function renderSwitcher() {
  return render(
    <ThemeModeProvider initialMode="dark">
      <AccountSwitcher />
    </ThemeModeProvider>,
  );
}

afterEach(() => {
  cleanup();
  useOptionalAccountsMock.mockReset();
});

describe('AccountSwitcher', () => {
  it('renders the reserved empty slot with no provider', () => {
    useOptionalAccountsMock.mockReturnValue(null);

    renderSwitcher();

    expect(screen.getByTestId('mode-chip-slot').dataset.filled).toBe('false');
    expect(screen.queryByTestId('account-switcher')).toBeNull();
  });

  it('keeps the slot reserved while the roster is loading', () => {
    useOptionalAccountsMock.mockReturnValue({ status: 'loading' });

    renderSwitcher();

    expect(screen.getByTestId('mode-chip-slot').dataset.filled).toBe('false');
  });

  it('shows the active account and its resolved mode', () => {
    const active = account('a1', TradingMode.Live);
    useOptionalAccountsMock.mockReturnValue(ready([active], active));

    renderSwitcher();

    expect(screen.getByTestId('account-switcher').textContent).toContain('Account a1');
    expect(screen.getByTestId('mode-chip').dataset.mode).toBe('Live');
  });

  it('switches the active account from the menu', () => {
    const setActiveAccount = vi.fn();
    const a1 = account('a1', TradingMode.Practice);
    const a2 = account('a2', TradingMode.Live);
    useOptionalAccountsMock.mockReturnValue(ready([a1, a2], a1, setActiveAccount));

    renderSwitcher();
    fireEvent.click(screen.getByTestId('account-switcher'));
    fireEvent.click(screen.getAllByTestId('account-option')[1]);

    expect(setActiveAccount).toHaveBeenCalledWith('a2');
  });

  it('surfaces an undeclared active account as a blocking explanation', () => {
    const active = account('a1', TradingMode.Undeclared);
    useOptionalAccountsMock.mockReturnValue(ready([active], active));

    renderSwitcher();
    fireEvent.click(screen.getByTestId('account-switcher'));

    expect(screen.getByTestId('undeclared-block')).toBeTruthy();
  });
});
