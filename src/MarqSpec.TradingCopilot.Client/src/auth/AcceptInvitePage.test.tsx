import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ApiResult } from '../api/client';
import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import { AcceptInvitePage } from './AcceptInvitePage';
import { useAuth } from './AuthProvider';

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

function renderAccept(value: Auth, entry = '/accept-invite') {
  useAuthMock.mockReturnValue(value);
  return render(
    <ThemeModeProvider initialMode="dark">
      <MemoryRouter initialEntries={[entry]}>
        <Routes>
          <Route path="/accept-invite" element={<AcceptInvitePage />} />
          <Route path="/" element={<div data-testid="dest">WORKSPACE</div>} />
        </Routes>
      </MemoryRouter>
    </ThemeModeProvider>,
  );
}

// MUI decorates a required field's label with an aria-hidden asterisk; match on the substring.
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

describe('AcceptInvitePage', () => {
  it('prefills the token from the invitation link', () => {
    renderAccept(auth(), '/accept-invite?token=invite-abc');

    expect((field('Invitation token') as HTMLInputElement).value).toBe('invite-abc');
  });

  it('redeems the invitation with the token, display name, and chosen password', () => {
    const acceptInvite = vi
      .fn()
      .mockResolvedValue({ ok: true, data: undefined } satisfies ApiResult<void>);
    renderAccept(auth({ acceptInvite }), '/accept-invite?token=invite-abc');

    fill('Display name', 'New Operator');
    fill('Choose a password', 'a strong one');
    fireEvent.click(screen.getByRole('button', { name: 'Accept invitation' }));

    expect(acceptInvite).toHaveBeenCalledWith({
      token: 'invite-abc',
      password: 'a strong one',
      displayName: 'New Operator',
    });
  });

  it('lands on the workspace after redeeming', async () => {
    const acceptInvite = vi
      .fn()
      .mockResolvedValue({ ok: true, data: undefined } satisfies ApiResult<void>);
    renderAccept(auth({ acceptInvite }), '/accept-invite?token=t');

    fill('Display name', 'New Operator');
    fill('Choose a password', 'pw');
    fireEvent.click(screen.getByRole('button', { name: 'Accept invitation' }));

    expect((await screen.findByTestId('dest')).textContent).toBe('WORKSPACE');
  });

  it('surfaces the invitation reason on a refusal — it is about the invite, not an account', async () => {
    const acceptInvite = vi.fn().mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'This invitation is invalid, already used, or expired.',
    } satisfies ApiResult<void>);
    renderAccept(auth({ acceptInvite }), '/accept-invite?token=stale');

    fill('Display name', 'New Operator');
    fill('Choose a password', 'pw');
    fireEvent.click(screen.getByRole('button', { name: 'Accept invitation' }));

    expect((await screen.findByRole('alert')).textContent).toBe(
      'This invitation is invalid, already used, or expired.',
    );
    expect(screen.queryByTestId('dest')).toBeNull();
  });

  it('shows a retry message when the request could not be sent', async () => {
    const acceptInvite = vi
      .fn()
      .mockResolvedValue({ ok: false, kind: 'failed', error: 'x' } satisfies ApiResult<void>);
    renderAccept(auth({ acceptInvite }), '/accept-invite?token=t');

    fill('Display name', 'New Operator');
    fill('Choose a password', 'pw');
    fireEvent.click(screen.getByRole('button', { name: 'Accept invitation' }));

    expect((await screen.findByRole('alert')).textContent).toContain('Check your connection');
  });

  it('redirects to the workspace when already signed in', () => {
    renderAccept(
      auth({
        session: {
          status: 'authenticated',
          user: { id: '1', email: 'op@local', displayName: 'Op' },
        },
      }),
      '/accept-invite?token=t',
    );

    expect(screen.getByTestId('dest').textContent).toBe('WORKSPACE');
    expect(screen.queryByLabelText('Choose a password', { exact: false })).toBeNull();
  });
});
