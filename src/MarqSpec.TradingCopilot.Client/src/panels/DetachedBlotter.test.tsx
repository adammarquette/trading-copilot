import { cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { useOptionalAccounts } from '../accounts/AccountProvider';
import { renderWithProviders } from '../testing/render';
import { DetachedBlotter } from './DetachedBlotter';

// Globals off ⇒ no RTL auto-cleanup; clear the DOM between renders so the two account states don't both linger.
afterEach(cleanup);

vi.mock('../accounts/AccountProvider', () => ({ useOptionalAccounts: vi.fn() }));
// The blotter itself has its own suite; here it is a stub so this test is about the account-sourcing wrapper.
vi.mock('../blotter/Blotter', () => ({
  Blotter: ({ accountId }: { accountId: string }) => (
    <div data-testid="blotter-stub">{accountId}</div>
  ),
}));

const accounts = vi.mocked(useOptionalAccounts);

describe('DetachedBlotter', () => {
  it('renders the blotter for the active account once one resolves', () => {
    accounts.mockReturnValue({
      status: 'ready',
      activeAccount: { id: 'acct-9' },
    } as ReturnType<typeof useOptionalAccounts>);

    renderWithProviders(<DetachedBlotter />);

    expect(screen.getByTestId('blotter-stub').textContent).toBe('acct-9');
  });

  it('withholds the blotter until an account resolves — never an empty blotter read as a false "flat"', () => {
    accounts.mockReturnValue(null);

    renderWithProviders(<DetachedBlotter />);

    expect(screen.queryByTestId('blotter-stub')).toBeNull();
    expect(screen.getByTestId('detached-blotter-no-account')).toBeTruthy();
  });

  it('surfaces an account-load error with a retry — the lean pop-out has no switcher to recover from', () => {
    // Without this, an errored account load in a detached window is invisible AND unrecoverable: the reload lives on
    // the app-bar switcher the lean frame omits. Still no blotter, so it never reads as flat.
    const reload = vi.fn();
    accounts.mockReturnValue({
      status: 'error',
      message: 'the venue is unreachable',
      reload,
    } as ReturnType<typeof useOptionalAccounts>);

    renderWithProviders(<DetachedBlotter />);

    expect(screen.queryByTestId('blotter-stub')).toBeNull();
    expect(screen.getByTestId('detached-blotter-error').textContent).toContain(
      'the venue is unreachable',
    );
    fireEvent.click(screen.getByRole('button', { name: /retry/i }));
    expect(reload).toHaveBeenCalled();
  });
});
