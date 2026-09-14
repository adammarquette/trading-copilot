import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ApiResult, CurrentUser, UnauthenticatedHandler } from '../api/client';
import {
  acceptInvite as apiAcceptInvite,
  getCurrentUser,
  setOnUnauthenticated,
  signIn as apiSignIn,
  signOut as apiSignOut,
} from '../api/client';
import { AuthProvider, useAuth } from './AuthProvider';

// The client is the seam; the provider is the unit. Mock the five operations it drives.
vi.mock('../api/client', () => ({
  getCurrentUser: vi.fn(),
  signIn: vi.fn(),
  acceptInvite: vi.fn(),
  signOut: vi.fn(),
  setOnUnauthenticated: vi.fn(),
}));

const getCurrentUserMock = vi.mocked(getCurrentUser);
const apiSignInMock = vi.mocked(apiSignIn);
const apiAcceptInviteMock = vi.mocked(apiAcceptInvite);
const apiSignOutMock = vi.mocked(apiSignOut);
const setOnUnauthenticatedMock = vi.mocked(setOnUnauthenticated);

const OPERATOR: CurrentUser = {
  id: 'op-1',
  email: 'operator@local',
  displayName: 'Primary Operator',
};

const anonymous: ApiResult<CurrentUser> = { ok: false, kind: 'failed', status: 401, error: 'x' };
const authenticated: ApiResult<CurrentUser> = { ok: true, data: OPERATOR };

/** Surfaces the session and its actions so a test can read state and fire the transitions. */
function Harness() {
  const { session, signIn, acceptInvite, signOut } = useAuth();
  return (
    <div>
      <span data-testid="status">{session.status}</span>
      <span data-testid="user">
        {session.status === 'authenticated' ? session.user.displayName : ''}
      </span>
      <button onClick={() => void signIn({ email: 'e', password: 'p' })}>do-signin</button>
      <button onClick={() => void acceptInvite({ token: 't', password: 'p', displayName: 'd' })}>
        do-accept
      </button>
      <button onClick={signOut}>do-signout</button>
    </div>
  );
}

function renderProvider() {
  return render(
    <AuthProvider>
      <Harness />
    </AuthProvider>,
  );
}

function status() {
  return screen.getByTestId('status').textContent;
}

beforeEach(() => {
  window.localStorage.clear();
  // The client operations are module-level mocks; clear their call history so counts are per-test.
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
});

describe('AuthProvider', () => {
  it('starts in a loading state while the identity probe is in flight', () => {
    getCurrentUserMock.mockReturnValue(new Promise<ApiResult<CurrentUser>>(() => {}));

    renderProvider();

    expect(status()).toBe('loading');
  });

  it('establishes an authenticated session from a stored token on load', async () => {
    getCurrentUserMock.mockResolvedValue(authenticated);

    renderProvider();

    await waitFor(() => expect(status()).toBe('authenticated'));
    expect(screen.getByTestId('user').textContent).toBe('Primary Operator');
  });

  it('settles to anonymous when there is no valid session', async () => {
    getCurrentUserMock.mockResolvedValue(anonymous);

    renderProvider();

    await waitFor(() => expect(status()).toBe('anonymous'));
  });

  it('establishes the session on a successful sign-in, reading the identity back', async () => {
    getCurrentUserMock.mockResolvedValueOnce(anonymous).mockResolvedValueOnce(authenticated);
    apiSignInMock.mockResolvedValue({ ok: true, data: undefined });

    renderProvider();
    await waitFor(() => expect(status()).toBe('anonymous'));

    fireEvent.click(screen.getByText('do-signin'));

    await waitFor(() => expect(status()).toBe('authenticated'));
    expect(apiSignInMock).toHaveBeenCalledWith({ email: 'e', password: 'p' });
    expect(screen.getByTestId('user').textContent).toBe('Primary Operator');
  });

  it('stays anonymous and does not re-probe when sign-in is rejected', async () => {
    getCurrentUserMock.mockResolvedValueOnce(anonymous);
    apiSignInMock.mockResolvedValue({ ok: false, kind: 'failed', status: 401, error: 'bad' });

    renderProvider();
    await waitFor(() => expect(status()).toBe('anonymous'));

    fireEvent.click(screen.getByText('do-signin'));

    await waitFor(() => expect(apiSignInMock).toHaveBeenCalledOnce());
    expect(status()).toBe('anonymous');
    // Only the mount probe — a failed grant must not read the identity back.
    expect(getCurrentUserMock).toHaveBeenCalledTimes(1);
  });

  it('establishes the session on a redeemed invitation', async () => {
    getCurrentUserMock.mockResolvedValueOnce(anonymous).mockResolvedValueOnce(authenticated);
    apiAcceptInviteMock.mockResolvedValue({ ok: true, data: undefined });

    renderProvider();
    await waitFor(() => expect(status()).toBe('anonymous'));

    fireEvent.click(screen.getByText('do-accept'));

    await waitFor(() => expect(status()).toBe('authenticated'));
    expect(apiAcceptInviteMock).toHaveBeenCalledWith({
      token: 't',
      password: 'p',
      displayName: 'd',
    });
  });

  it('drops the token and goes anonymous on sign-out', async () => {
    getCurrentUserMock.mockResolvedValue(authenticated);

    renderProvider();
    await waitFor(() => expect(status()).toBe('authenticated'));

    fireEvent.click(screen.getByText('do-signout'));

    await waitFor(() => expect(status()).toBe('anonymous'));
    expect(apiSignOutMock).toHaveBeenCalledOnce();
  });

  it('drops to anonymous when the client signals a mid-session 401', async () => {
    getCurrentUserMock.mockResolvedValue(authenticated);

    renderProvider();
    await waitFor(() => expect(status()).toBe('authenticated'));

    // The provider wired this handler; a protected 401 anywhere in the app invokes it.
    const handler = setOnUnauthenticatedMock.mock.calls.at(-1)?.[0] as UnauthenticatedHandler;
    await act(async () => {
      handler();
    });

    expect(status()).toBe('anonymous');
  });
});
