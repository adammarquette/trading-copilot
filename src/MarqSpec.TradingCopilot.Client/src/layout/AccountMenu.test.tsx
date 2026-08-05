import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { useOptionalAuth } from '../auth/AuthProvider';
import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import { AccountMenu } from './AccountMenu';

vi.mock('../auth/AuthProvider', () => ({ useOptionalAuth: vi.fn() }));
const useOptionalAuthMock = vi.mocked(useOptionalAuth);

type Auth = NonNullable<ReturnType<typeof useOptionalAuth>>;

function authValue(session: Auth['session'], signOut = vi.fn()): Auth {
  return { session, signIn: vi.fn(), acceptInvite: vi.fn(), signOut };
}

function renderMenu() {
  return render(
    <ThemeModeProvider initialMode="dark">
      <AccountMenu />
    </ThemeModeProvider>,
  );
}

afterEach(() => {
  cleanup();
  useOptionalAuthMock.mockReset();
});

describe('AccountMenu', () => {
  it('renders nothing without a provider — the shell mounts bare in some tests', () => {
    useOptionalAuthMock.mockReturnValue(null);

    renderMenu();

    expect(screen.queryByTestId('account-menu-trigger')).toBeNull();
  });

  it('renders nothing until the session is authenticated', () => {
    useOptionalAuthMock.mockReturnValue(authValue({ status: 'loading' }));
    renderMenu();
    expect(screen.queryByTestId('account-menu-trigger')).toBeNull();

    useOptionalAuthMock.mockReturnValue(authValue({ status: 'anonymous' }));
    renderMenu();
    expect(screen.queryByTestId('account-menu-trigger')).toBeNull();
  });

  it('names the signed-in operator and offers a sign-out', () => {
    useOptionalAuthMock.mockReturnValue(
      authValue({
        status: 'authenticated',
        user: { id: '1', email: 'operator@local', displayName: 'Primary Operator' },
      }),
    );

    renderMenu();
    fireEvent.click(screen.getByTestId('account-menu-trigger'));

    expect(screen.getByText('Primary Operator')).toBeTruthy();
    expect(screen.getByText('operator@local')).toBeTruthy();
    expect(screen.getByTestId('sign-out')).toBeTruthy();
  });

  it('signs out when the sign-out item is chosen', () => {
    const signOut = vi.fn();
    useOptionalAuthMock.mockReturnValue(
      authValue(
        {
          status: 'authenticated',
          user: { id: '1', email: 'operator@local', displayName: 'Primary Operator' },
        },
        signOut,
      ),
    );

    renderMenu();
    fireEvent.click(screen.getByTestId('account-menu-trigger'));
    fireEvent.click(screen.getByTestId('sign-out'));

    expect(signOut).toHaveBeenCalledOnce();
  });
});
