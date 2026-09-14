import { act, cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { useAccounts } from '../accounts/AccountProvider';
import { TradingMode } from '../api/accounts';
import {
  listActionableSuggestions,
  OrderSide,
  type Suggestion,
  SuggestionState,
  takeSuggestion,
  SuggestionOrigin,
} from '../api/suggestions';
import { renderWithProviders } from '../testing/render';
import { SuggestionsPanel } from './SuggestionsPanel';

vi.mock('../accounts/AccountProvider', () => ({ useAccounts: vi.fn() }));

vi.mock('../api/suggestions', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/suggestions')>()),
  listActionableSuggestions: vi.fn(),
  passSuggestion: vi.fn(),
  takeSuggestion: vi.fn(),
}));

// The hosted SuggestionList subscribes to realtime (gh#760); these tests never fire a push, so a no-op stub is
// enough to satisfy the hook outside a RealtimeProvider.
const { useRealtimeMock } = vi.hoisted(() => ({ useRealtimeMock: vi.fn() }));
vi.mock('../realtime/RealtimeProvider', () => ({ useRealtime: useRealtimeMock }));

const accountsMock = vi.mocked(useAccounts);
const listMock = vi.mocked(listActionableSuggestions);

function suggestion(id: string, accountId: string): Suggestion {
  return {
    id,
    accountId,
    instrument: 'ES',
    timeframeMinutes: 15,
    side: OrderSide.Buy,
    size: 1,
    entryPrice: 5230.25,
    stopPrice: 5222,
    targetPrice: 5248.5,
    mode: TradingMode.Practice,
    state: SuggestionState.Active,
    createdAt: '2026-08-05T13:41:22.000Z',
    rewardRiskRatio: 2.2,
    riskUsd: 206,
    rewardUsd: 456,
    rationale: `Rationale for ${id}.`,
    origin: SuggestionOrigin.Scan,
    citedIndicator: 'RSI',
    citedPeriod: 14,
    citedResolutionMinutes: 15,
    confidence: 50,
    expiresAt: new Date(Date.now() + 300_000).toISOString(),
    stateChangedAt: null,
    version: 1,
    supersedesId: null,
    disposition: null,
  } as Suggestion;
}

/** An account context in its `ready` state, for the given active account. */
function ready(accountId: string) {
  return {
    status: 'ready',
    activeAccount: { id: accountId, mode: TradingMode.Practice },
  } as unknown as ReturnType<typeof useAccounts>;
}

/** A promise whose resolution this test controls, so responses can be settled OUT OF ORDER. */
function deferred<T>() {
  let settle!: (value: T) => void;
  const promise = new Promise<T>((resolve) => {
    settle = resolve;
  });
  return { promise, settle };
}

beforeEach(() => {
  vi.clearAllMocks();
  useRealtimeMock.mockReturnValue({
    connectionState: 'live',
    onEvent: vi.fn(() => vi.fn()),
    onOrderState: vi.fn(() => vi.fn()),
    onFill: vi.fn(() => vi.fn()),
    onSuggestion: vi.fn(() => vi.fn()),
    onResync: vi.fn(() => vi.fn()),
  });
  vi.mocked(takeSuggestion).mockResolvedValue({ ok: false, kind: 'failed', error: 'unused' });
});

afterEach(cleanup);

describe('SuggestionsPanel — account scoping (R-14, gh#713)', () => {
  it('never lets a slow response for the account just left replace the current account list', async () => {
    // THE GUARD. A stale response landing last is the whole defect: the operator switches to a practice account,
    // the live account's slower request resolves afterwards, and its cards render WITH THEIR ACTIONS ENABLED.
    // `POST /take` arms against the suggestion's OWN account server-side, so acting on those cards arms on the
    // account the operator believes they left — the mode chip being the only tell, and the one thing a person
    // stops reading once they think they have already switched.
    //
    // Ordering is the defect, so the test controls it: account A's promise is settled LAST, on purpose. A test
    // that merely asserts both fetches happened would pass against the bug.
    const live = deferred<Awaited<ReturnType<typeof listActionableSuggestions>>>();
    const practice = deferred<Awaited<ReturnType<typeof listActionableSuggestions>>>();
    listMock
      .mockImplementationOnce(() => live.promise)
      .mockImplementationOnce(() => practice.promise);

    accountsMock.mockReturnValue(ready('acc-live'));
    const view = renderWithProviders(<SuggestionsPanel />);

    // The operator switches before the first response has arrived.
    accountsMock.mockReturnValue(ready('acc-practice'));
    view.rerender(<SuggestionsPanel />);

    practice.settle({ ok: true, data: [suggestion('s-practice', 'acc-practice')] });
    await act(async () => {});
    expect(await screen.findByText(/Rationale for s-practice\./)).toBeDefined();

    // ...and only now does the account they left answer.
    live.settle({ ok: true, data: [suggestion('s-live', 'acc-live')] });
    await act(async () => {});

    expect(screen.queryByText(/Rationale for s-live\./)).toBeNull();
    expect(screen.queryByText(/Rationale for s-practice\./)).not.toBeNull();
  });

  it('asks for the account that is active, not the one that was', async () => {
    // The companion to the above: the switch must actually re-request. Without this, a surface that never
    // refetched would pass the stale-response guard vacuously — by showing nothing at all.
    listMock.mockResolvedValue({ ok: true, data: [] });

    accountsMock.mockReturnValue(ready('acc-live'));
    const view = renderWithProviders(<SuggestionsPanel />);
    await act(async () => {});

    accountsMock.mockReturnValue(ready('acc-practice'));
    view.rerender(<SuggestionsPanel />);
    await act(async () => {});

    expect(listMock.mock.calls.map(([accountId]) => accountId)).toEqual([
      'acc-live',
      'acc-practice',
    ]);
  });
});

describe('SuggestionsPanel — armed handoff to the order ticket (gh#655)', () => {
  it('hands a taken suggestion to the review → send surface', async () => {
    // The "where it comes together" step. The card arms (POST /take stages a ticket) and hands off; the panel must
    // then present that staged ticket for review and sending — the send does not happen on the card.
    listMock.mockResolvedValue({ ok: true, data: [suggestion('s-1', 'acc-1')] });
    vi.mocked(takeSuggestion).mockResolvedValue({
      ok: true,
      data: {
        orderId: 'o1',
        status: 'Staged',
        outcome: 'Allowed',
        approvedQuantity: 1,
        bindingLayer: null,
        reason: 'Within every layer.',
        target: 5248.5,
      },
    });
    accountsMock.mockReturnValue(ready('acc-1'));

    renderWithProviders(<SuggestionsPanel />);
    await act(async () => {});
    await screen.findByText(/Rationale for s-1\./);

    // No live-quote surface yet (gh#645/#646), so the operator supplies the market price, then arms.
    await act(async () => {
      fireEvent.change(screen.getByTestId('reference-price'), { target: { value: '5231' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId('take-button'));
    });

    // The take staged a ticket; the panel now shows the review → send surface for it, with a send control.
    expect(await screen.findByTestId('staged-ticket-review')).toBeTruthy();
    expect(screen.getByRole('button', { name: /^send$/i })).toBeTruthy();
  });
});
