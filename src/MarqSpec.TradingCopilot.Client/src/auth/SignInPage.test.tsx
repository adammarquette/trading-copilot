import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { Location } from 'react-router';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ApiResult } from '../api/client';
import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import { useAuth } from './AuthProvider';
import { SignInPage } from './SignInPage';

// The page is the unit under test; the session is a seam. Mock it so each test dictates what sign-in returns.
vi.mock('./AuthProvider', () => ({ useAuth: vi.fn() }));
const useAuthMock = vi.mocked(useAuth);

type Auth = ReturnType<typeof useAuth>;

function auth(overrides: Partial<Auth> = {}): Auth {
  return {
    session: { status: 'anonymous' },
    signIn: vi.fn(),
    acceptInvite: vi.fn(),
    signOut: vi.fn(),
    ...overrides,
  };
}

function renderSignIn(
  value: Auth,
  initialEntries: Array<string | Partial<Location>> = ['/sign-in'],
) {
  useAuthMock.mockReturnValue(value);
  return render(
    <ThemeModeProvider initialMode="dark">
      <MemoryRouter initialEntries={initialEntries}>
        <Routes>
          <Route path="/sign-in" element={<SignInPage />} />
          <Route path="/" element={<div data-testid="dest">WORKSPACE</div>} />
          <Route path="/journal" element={<div data-testid="dest">JOURNAL</div>} />
        </Routes>
      </MemoryRouter>
    </ThemeModeProvider>,
  );
}

// MUI marks a required field's label with an aria-hidden asterisk, so the label text is "Email *", not
// "Email". Match on the substring rather than pinning the exact decorated text.
function field(label: string) {
  return screen.getByLabelText(label, { exact: false });
}

function fill(label: string, value: string) {
  fireEvent.change(field(label), { target: { value } });
}

beforeEach(() => {
  window.localStorage.clear();
});

afterEach(() => {
  cleanup();
  useAuthMock.mockReset();
});

describe('SignInPage', () => {
  it('renders the credential form and nothing that names an account', () => {
    renderSignIn(auth());

    expect(field('Email')).toBeTruthy();
    expect(field('Password')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeTruthy();
    // A credential surface carries no account state — no sign-out, no operator identity to shoulder-surf.
    expect(screen.queryByRole('button', { name: 'Account' })).toBeNull();
  });

  it('hands the typed credentials to signIn', () => {
    const signIn = vi
      .fn()
      .mockResolvedValue({ ok: true, data: undefined } satisfies ApiResult<void>);
    renderSignIn(auth({ signIn }));

    fill('Email', 'operator@local');
    fill('Password', 'correct horse');
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(signIn).toHaveBeenCalledWith({ email: 'operator@local', password: 'correct horse' });
  });

  it('lands on the workspace after a successful sign-in', async () => {
    const signIn = vi
      .fn()
      .mockResolvedValue({ ok: true, data: undefined } satisfies ApiResult<void>);
    renderSignIn(auth({ signIn }));

    fill('Email', 'operator@local');
    fill('Password', 'pw');
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect((await screen.findByTestId('dest')).textContent).toBe('WORKSPACE');
  });

  it('returns to the surface the operator was headed for', async () => {
    // RequireAuth stashes it in location.state.from; sign-in honours it.
    const signIn = vi
      .fn()
      .mockResolvedValue({ ok: true, data: undefined } satisfies ApiResult<void>);
    renderSignIn(auth({ signIn }), [{ pathname: '/sign-in', state: { from: '/journal' } }]);

    fill('Email', 'operator@local');
    fill('Password', 'pw');
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect((await screen.findByTestId('dest')).textContent).toBe('JOURNAL');
  });

  it('reports a rejected login without revealing whether the account exists', async () => {
    // A wrong password and an unknown email both come back as failed/401, and must read identically.
    const signIn = vi.fn().mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 401,
      error: 'x',
    } satisfies ApiResult<void>);
    renderSignIn(auth({ signIn }));

    fill('Email', 'stranger@nowhere');
    fill('Password', 'guess');
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toBe('The email or password is incorrect.');
    expect(alert.textContent).not.toContain('stranger@nowhere');
    expect(screen.queryByTestId('dest')).toBeNull();
  });

  it('recovers the form when a 2xx carries no session token — no dead submit button (gh#954)', async () => {
    // The end the gh#954 defect was felt at. `signIn` used to THROW here (an empty 2xx body dereferenced as
    // `undefined.token`), and since `onSubmit` awaits without a try/catch, `setSubmitting(false)` never ran: the
    // button stayed disabled with no alert and no way forward, on the surface that gates every other surface.
    // The seam now answers `failed`, so this asserts the two things that were missing — an alert IS shown, and
    // the button is usable again. Deliberately not a `.rejects` test: the point is that nothing throws at all.
    const signIn = vi.fn().mockResolvedValue({
      ok: false,
      kind: 'failed',
      error: 'Sign-in did not return a session token.',
    } satisfies ApiResult<void>);
    renderSignIn(auth({ signIn }));

    fill('Email', 'operator@local');
    fill('Password', 'pw');
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toBeTruthy();
    const button = screen.getByRole('button', { name: 'Sign in' });
    expect((button as HTMLButtonElement).disabled).toBe(false);
  });

  it('distinguishes a connection failure from a rejected credential', async () => {
    const signIn = vi.fn().mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 503,
      error: 'x',
    } satisfies ApiResult<void>);
    renderSignIn(auth({ signIn }));

    fill('Email', 'operator@local');
    fill('Password', 'pw');
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Check your connection');
  });

  it('redirects away from the credential surface when already signed in', () => {
    renderSignIn(
      auth({
        session: {
          status: 'authenticated',
          user: { id: '1', email: 'op@local', displayName: 'Op' },
        },
      }),
    );

    expect(screen.getByTestId('dest').textContent).toBe('WORKSPACE');
    expect(screen.queryByLabelText('Email', { exact: false })).toBeNull();
  });
});
