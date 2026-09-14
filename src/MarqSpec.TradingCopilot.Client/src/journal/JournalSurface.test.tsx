import { cleanup, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { type AccountContextValue, useAccounts } from '../accounts/AccountProvider';
import { destinations } from '../navigation/destinations';
import { renderWithProviders } from '../testing/render';
import { JournalSurface } from './JournalSurface';

vi.mock('../accounts/AccountProvider', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../accounts/AccountProvider')>()),
  useAccounts: vi.fn(),
}));

vi.mock('./JournalMonth', () => ({
  JournalMonth: ({ accountId, today }: { accountId: string; today: string }) => (
    <div data-testid="journal-month">{`${accountId}/${today}`}</div>
  ),
}));

const accountsMock = vi.mocked(useAccounts);

const DESTINATION = destinations.find((candidate) => candidate.id === 'journal')!;

const ACCOUNT = { id: 'a1', name: 'Combine 50k' };

function ready(): AccountContextValue {
  return {
    status: 'ready',
    accounts: [ACCOUNT] as never,
    activeAccount: ACCOUNT as never,
    setActiveAccount: vi.fn(),
    reload: vi.fn(),
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  accountsMock.mockReturnValue(ready());
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe('JournalSurface', () => {
  it('carries the shell\u2019s surface contract in every state', () => {
    // AppRoutes keys navigation off `data-surface`; a surface that only wears it once loaded shows as a
    // missing surface while the account roster resolves.
    accountsMock.mockReturnValue({ status: 'loading' });

    renderWithProviders(<JournalSurface destination={DESTINATION} />);

    expect(screen.getByTestId('surface').dataset.surface).toBe('journal');
    expect(screen.getByTestId('loading-state')).toBeTruthy();
  });

  it('scopes the journal to the active account (R-14)', () => {
    // A journal is per account, and the endpoint scopes to that account's OWN current mode. Rendering it
    // without an account would be a report of nothing in particular.
    renderWithProviders(<JournalSurface destination={DESTINATION} />);

    expect(screen.getByTestId('journal-month').textContent).toMatch(/^a1\//);
  });

  it('opens the journal on the Central trading day, not the browser\u2019s', () => {
    // The endpoint groups by the Central calendar day; defaulting to the operator's own date would open
    // the journal on a day the server reports nothing for.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-09-05T02:00:00Z'));

    renderWithProviders(<JournalSurface destination={DESTINATION} />);

    expect(screen.getByTestId('journal-month').textContent).toBe('a1/2026-09-04');
  });

  it('says there is no account to journal rather than showing an empty calendar', () => {
    accountsMock.mockReturnValue({ status: 'empty', reload: vi.fn() });

    renderWithProviders(<JournalSurface destination={DESTINATION} />);

    expect(screen.getByText('No account to journal')).toBeTruthy();
  });

  it('surfaces an account-context failure with a retry', () => {
    const reload = vi.fn();
    accountsMock.mockReturnValue({ status: 'error', message: 'roster unavailable', reload });

    renderWithProviders(<JournalSurface destination={DESTINATION} />);

    expect(screen.getByText('roster unavailable')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });
});
