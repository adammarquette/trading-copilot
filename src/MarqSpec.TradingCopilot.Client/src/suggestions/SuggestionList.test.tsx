import { act, cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { TradingMode } from '../api/accounts';
import {
  listActionableSuggestions,
  OrderSide,
  passSuggestion,
  type Suggestion,
  SuggestionState,
  takeSuggestion,
} from '../api/suggestions';
import { renderWithProviders } from '../testing/render';
import { SuggestionList } from './SuggestionList';

vi.mock('../api/suggestions', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/suggestions')>()),
  listActionableSuggestions: vi.fn(),
  passSuggestion: vi.fn(),
  takeSuggestion: vi.fn(),
}));

const { useRealtimeMock } = vi.hoisted(() => ({ useRealtimeMock: vi.fn() }));
vi.mock('../realtime/RealtimeProvider', () => ({ useRealtime: useRealtimeMock }));

const listMock = vi.mocked(listActionableSuggestions);
const passMock = vi.mocked(passSuggestion);

// The realtimeSuggestion / resync handlers the list subscribes — captured so a test can fire either as the socket would.
let suggestionHandler: (() => void) | null = null;
let resyncHandler: (() => void) | null = null;

function suggestion(id: string, overrides: Partial<Suggestion> = {}): Suggestion {
  return {
    id,
    accountId: 'acc-1',
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
    citedIndicator: 'RSI',
    citedPeriod: 14,
    citedResolutionMinutes: 15,
    confidence: 50,
    expiresAt: new Date(Date.now() + 300_000).toISOString(),
    stateChangedAt: null,
    version: 1,
    supersedesId: null,
    disposition: null,
    ...overrides,
  };
}

/** Renders and lets the load settle — the list opens on `loading` and resolves in a microtask. */
async function renderList() {
  const view = renderWithProviders(<SuggestionList accountId="acc-1" />);
  await act(async () => {});
  return view;
}

beforeEach(() => {
  vi.clearAllMocks();
  suggestionHandler = null;
  resyncHandler = null;
  useRealtimeMock.mockReturnValue({
    connectionState: 'live',
    onEvent: vi.fn(),
    onOrderState: vi.fn(),
    onFill: vi.fn(),
    onSuggestion: (handler: () => void) => {
      suggestionHandler = handler;
      return vi.fn();
    },
    onResync: (handler: () => void) => {
      resyncHandler = handler;
      return vi.fn();
    },
  });
  vi.mocked(takeSuggestion).mockResolvedValue({
    ok: false,
    kind: 'failed',
    error: 'not used here',
  });
  passMock.mockResolvedValue({
    ok: true,
    data: {
      suggestionId: 's-1',
      kind: 3,
      reasons: 0,
      deviations: 0,
      takenEntryPrice: null,
      takenStopPrice: null,
      takenTargetPrice: null,
      takenSize: null,
      note: null,
      createdAt: '2026-08-05T13:45:00.000Z',
    },
  });
});

afterEach(() => {
  cleanup();
});

describe('SuggestionList', () => {
  it('shows a loading affordance before the first answer arrives', () => {
    listMock.mockReturnValue(new Promise(() => {}));

    renderWithProviders(<SuggestionList accountId="acc-1" />);

    expect(screen.getByTestId('loading-state')).toBeTruthy();
  });

  it('renders the page in the SERVER’s order, and never re-sorts by confidence', async () => {
    // Sorting a decision list by the model's own self-assessment is the subtlest way to turn a display-only
    // figure into a recommendation (R-4). The low-confidence card comes back first, so it renders first.
    listMock.mockResolvedValue({
      ok: true,
      data: [suggestion('s-1', { confidence: 5 }), suggestion('s-2', { confidence: 95 })],
    });

    await renderList();

    const ids = screen.getAllByTestId('suggestion-card').map((card) => card.dataset.suggestionId);
    expect(ids).toEqual(['s-1', 's-2']);
  });

  it('tells "nothing proposed" apart from "the load broke"', async () => {
    // An operator who reads one as the other trades on nothing, or waits for a feed that is already answering.
    listMock.mockResolvedValue({ ok: true, data: [] });

    await renderList();

    expect(screen.getByTestId('empty-state').textContent).toContain('No setup right now');
    expect(screen.queryByTestId('suggestions-retry')).toBeNull();
  });

  it('surfaces a failed load with a retry', async () => {
    listMock.mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 503,
      error: 'BFF unreachable.',
    });

    await renderList();
    expect(screen.getByTestId('empty-state').textContent).toContain('BFF unreachable.');

    listMock.mockResolvedValue({ ok: true, data: [suggestion('s-1')] });
    await act(async () => {
      fireEvent.click(screen.getByTestId('suggestions-retry'));
    });

    expect(screen.getAllByTestId('suggestion-card')).toHaveLength(1);
  });

  it('drops a passed card out of the actionable list, leaving the rest', async () => {
    // The disposition is recorded; the setup is no longer something to decide on. It stays addressable by id —
    // `GET /suggestions/{id}` returns it in any state — but the decision surface is done with it.
    listMock.mockResolvedValue({ ok: true, data: [suggestion('s-1'), suggestion('s-2')] });

    await renderList();
    expect(screen.getAllByTestId('suggestion-card')).toHaveLength(2);

    await act(async () => {
      fireEvent.click(screen.getAllByTestId('pass-button')[0]);
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId('pass-skip'));
    });

    const remaining = screen
      .getAllByTestId('suggestion-card')
      .map((card) => card.dataset.suggestionId);
    expect(remaining).toEqual(['s-2']);
  });

  it('keeps a card whose pass was REFUSED — nothing was recorded, so nothing may disappear', async () => {
    passMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'This suggestion already has a disposition.',
    });
    listMock.mockResolvedValue({ ok: true, data: [suggestion('s-1')] });

    await renderList();
    await act(async () => {
      fireEvent.click(screen.getByTestId('pass-button'));
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId('pass-skip'));
    });

    expect(screen.getAllByTestId('suggestion-card')).toHaveLength(1);
    expect(screen.getByTestId('pass-refusal').textContent).toContain('already has a disposition');
  });

  it('reloads when the operator switches account — suggestions are account-scoped (R-14)', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });

    const view = renderWithProviders(<SuggestionList accountId="acc-1" />);
    await act(async () => {});
    view.rerender(<SuggestionList accountId="acc-2" />);
    await act(async () => {});

    expect(listMock.mock.calls.map((call) => call[0])).toEqual(['acc-1', 'acc-2']);
  });

  it('refetches the actionable list on a realtimeSuggestion push (gh#760)', async () => {
    listMock.mockResolvedValue({ ok: true, data: [suggestion('s-1')] });
    await renderList();
    expect(screen.getAllByTestId('suggestion-card')).toHaveLength(1);

    // A new / superseded suggestion arrives as a compact push — too little to render — so the panel refetches and
    // now returns two, without a poll or a manual reload (the R-4 decision surface stays live).
    listMock.mockResolvedValue({ ok: true, data: [suggestion('s-1'), suggestion('s-2')] });
    await act(async () => {
      suggestionHandler?.();
    });

    expect(screen.getAllByTestId('suggestion-card')).toHaveLength(2);
  });

  it('keeps the current list when a push-triggered refresh fails — a nudge never nukes a working panel', async () => {
    listMock.mockResolvedValue({ ok: true, data: [suggestion('s-1')] });
    await renderList();
    expect(screen.getAllByTestId('suggestion-card')).toHaveLength(1);

    // The background refresh breaks. Unlike the first load / retry, a failed soft refresh must not swap the panel
    // for an error screen — the operator keeps the setups already on the surface, and the retry owns the error.
    listMock.mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 503,
      error: 'BFF unreachable.',
    });
    await act(async () => {
      suggestionHandler?.();
    });

    expect(screen.getAllByTestId('suggestion-card')).toHaveLength(1);
    expect(screen.queryByTestId('empty-state')).toBeNull();
  });

  it('refetches on a resync — pushes missed during a socket drop are never replayed (R-19)', async () => {
    listMock.mockResolvedValue({ ok: true, data: [suggestion('s-1')] });
    await renderList();
    expect(screen.getAllByTestId('suggestion-card')).toHaveLength(1);

    // Owner-scoped pushes are live-only, so a reconnect / retention gap re-fetches the whole list rather than
    // trusting it survived the drop.
    listMock.mockResolvedValue({ ok: true, data: [suggestion('s-1'), suggestion('s-2')] });
    await act(async () => {
      resyncHandler?.();
    });

    expect(screen.getAllByTestId('suggestion-card')).toHaveLength(2);
  });
});
