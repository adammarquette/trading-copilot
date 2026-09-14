import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import { useAuth } from './AuthProvider';
import { RequireAuth } from './RequireAuth';

vi.mock('./AuthProvider', () => ({ useAuth: vi.fn() }));
const useAuthMock = vi.mocked(useAuth);

type Session = ReturnType<typeof useAuth>['session'];

/** Reports what `from` the guard remembered, so a redirect can be checked, not just observed. */
function SignInProbe() {
  const state = useLocation().state as { from?: string } | null;
  return <div data-testid="sign-in">from: {state?.from ?? '(none)'}</div>;
}

function renderGuardedAt(session: Session, entry = '/journal') {
  useAuthMock.mockReturnValue({
    session,
    signIn: vi.fn(),
    acceptInvite: vi.fn(),
    signOut: vi.fn(),
  });
  return render(
    <ThemeModeProvider initialMode="dark">
      <MemoryRouter initialEntries={[entry]}>
        <Routes>
          {/* The more-specific sign-in route is the redirect target; everything else is behind the guard,
              so any deep link (with its query string) exercises what RequireAuth remembers. */}
          <Route path="/sign-in" element={<SignInProbe />} />
          <Route element={<RequireAuth />}>
            <Route path="*" element={<div data-testid="protected">PROTECTED</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </ThemeModeProvider>,
  );
}

afterEach(() => {
  cleanup();
  useAuthMock.mockReset();
});

describe('RequireAuth', () => {
  it('holds on a loading state while the session is still being restored', () => {
    // Not the sign-in form: flashing sign-in and then the workspace on every reload is worse than a spinner.
    renderGuardedAt({ status: 'loading' });

    expect(screen.getByTestId('loading-state')).toBeTruthy();
    expect(screen.queryByTestId('protected')).toBeNull();
    expect(screen.queryByTestId('sign-in')).toBeNull();
  });

  it('renders the protected surface when authenticated', () => {
    renderGuardedAt({
      status: 'authenticated',
      user: { id: '1', email: 'op@local', displayName: 'Op' },
    });

    expect(screen.getByTestId('protected').textContent).toBe('PROTECTED');
  });

  it('redirects an anonymous operator to sign-in, remembering where they were headed', () => {
    renderGuardedAt({ status: 'anonymous' }, '/journal');

    expect(screen.queryByTestId('protected')).toBeNull();
    expect(screen.getByTestId('sign-in').textContent).toBe('from: /journal');
  });

  it('remembers the full path including the query string', () => {
    renderGuardedAt({ status: 'anonymous' }, '/accounts/1/risk?tab=limits');

    expect(screen.getByTestId('sign-in').textContent).toBe('from: /accounts/1/risk?tab=limits');
  });
});
