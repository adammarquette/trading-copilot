import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { type Account, listAllAccounts, TradingMode } from '../api/accounts';
import { AccountProvider, useAccounts } from './AccountProvider';

vi.mock('../api/accounts', async (orig) => ({
  ...(await orig<typeof import('../api/accounts')>()),
  listAllAccounts: vi.fn(),
}));
const listAllAccountsMock = vi.mocked(listAllAccounts);

function account(id: string, mode: TradingMode = TradingMode.Practice): Account {
  return {
    id,
    venueAccountKey: `V-${id}`,
    name: `Account ${id}`,
    stage: 1,
    stageOverride: null,
    mode,
    canTrade: true,
    isVisible: true,
    balance: 1000,
    tradeableHere: true,
  };
}

function Harness() {
  const ctx = useAccounts();
  return (
    <div>
      <span data-testid="status">{ctx.status}</span>
      <span data-testid="active">{ctx.status === 'ready' ? ctx.activeAccount.id : ''}</span>
      {ctx.status === 'ready' ? (
        <button onClick={() => ctx.setActiveAccount('a2')}>pick-a2</button>
      ) : null}
      {ctx.status !== 'loading' ? <button onClick={ctx.reload}>reload</button> : null}
    </div>
  );
}

function renderProvider() {
  return render(
    <AccountProvider>
      <Harness />
    </AccountProvider>,
  );
}

const status = () => screen.getByTestId('status').textContent;
const active = () => screen.getByTestId('active').textContent;

beforeEach(() => {
  localStorage.clear();
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
});

describe('AccountProvider', () => {
  it('starts loading while the roster is in flight', () => {
    listAllAccountsMock.mockReturnValue(new Promise(() => {}));

    renderProvider();

    expect(status()).toBe('loading');
  });

  it('resolves the first account active when nothing is stored', async () => {
    listAllAccountsMock.mockResolvedValue({ ok: true, data: [account('a1'), account('a2')] });

    renderProvider();

    await waitFor(() => expect(status()).toBe('ready'));
    expect(active()).toBe('a1');
  });

  it('restores the stored active account when it still exists', async () => {
    localStorage.setItem('tc.active-account', 'a2');
    listAllAccountsMock.mockResolvedValue({ ok: true, data: [account('a1'), account('a2')] });

    renderProvider();

    await waitFor(() => expect(active()).toBe('a2'));
  });

  it('falls back to a default when the stored account is gone — never a dangling selection', async () => {
    localStorage.setItem('tc.active-account', 'deleted');
    listAllAccountsMock.mockResolvedValue({ ok: true, data: [account('a1'), account('a2')] });

    renderProvider();

    await waitFor(() => expect(status()).toBe('ready'));
    expect(active()).toBe('a1');
  });

  it('switches the active account and persists the choice', async () => {
    listAllAccountsMock.mockResolvedValue({ ok: true, data: [account('a1'), account('a2')] });

    renderProvider();
    await waitFor(() => expect(status()).toBe('ready'));

    fireEvent.click(screen.getByText('pick-a2'));

    expect(active()).toBe('a2');
    expect(localStorage.getItem('tc.active-account')).toBe('a2');
  });

  it('reports empty when there are no accounts', async () => {
    listAllAccountsMock.mockResolvedValue({ ok: true, data: [] });

    renderProvider();

    await waitFor(() => expect(status()).toBe('empty'));
  });

  it('surfaces an error and can reload', async () => {
    listAllAccountsMock.mockResolvedValueOnce({
      ok: false,
      kind: 'failed',
      status: 503,
      error: 'down',
    });

    renderProvider();
    await waitFor(() => expect(status()).toBe('error'));

    listAllAccountsMock.mockResolvedValueOnce({ ok: true, data: [account('a1')] });
    fireEvent.click(screen.getByText('reload'));

    await waitFor(() => expect(status()).toBe('ready'));
    expect(active()).toBe('a1');
  });
});
