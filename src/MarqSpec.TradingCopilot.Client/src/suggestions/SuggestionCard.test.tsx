import { act, cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { TradingMode } from '../api/accounts';
import {
  OrderSide,
  type StagedTicket,
  type Suggestion,
  SuggestionState,
  passSuggestion,
  takeSuggestion,
} from '../api/suggestions';
import { renderWithProviders } from '../testing/render';
import { SuggestionCard } from './SuggestionCard';

/**
 * The two acts are mocked; everything else — the enums, the pass-reason flags, the formatters — is the real
 * module, so a rename in the contract still breaks this suite rather than being papered over by a stub.
 */
vi.mock('../api/suggestions', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/suggestions')>()),
  passSuggestion: vi.fn(),
  takeSuggestion: vi.fn(),
}));

const takeMock = vi.mocked(takeSuggestion);
const passMock = vi.mocked(passSuggestion);

const ISSUED_AT = '2026-08-05T13:41:22.000Z';

function suggestion(overrides: Partial<Suggestion> = {}): Suggestion {
  return {
    id: 's-1',
    accountId: 'acc-1',
    instrument: 'ES',
    timeframeMinutes: 15,
    side: OrderSide.Buy,
    size: 2,
    entryPrice: 5230.25,
    stopPrice: 5222,
    targetPrice: 5248.5,
    mode: TradingMode.Practice,
    state: SuggestionState.Active,
    createdAt: ISSUED_AT,
    rewardRiskRatio: 2.2,
    riskUsd: 412,
    rewardUsd: 912,
    rationale: 'Buy-side delta positive over the last 12 bars; reclaimed session VWAP and holding.',
    citedIndicator: 'RSI',
    citedPeriod: 14,
    citedResolutionMinutes: 15,
    confidence: 72,
    expiresAt: new Date(Date.now() + 300_000).toISOString(),
    stateChangedAt: null,
    version: 1,
    supersedesId: null,
    disposition: null,
    ...overrides,
  };
}

function ticket(overrides: Partial<StagedTicket> = {}): StagedTicket {
  return {
    orderId: 'o-1',
    status: 'Staged',
    outcome: 'Approved',
    approvedQuantity: 2,
    bindingLayer: null,
    reason: 'Within every limit.',
    target: 5248.5,
    ...overrides,
  };
}

/** Renders with a live reference already supplied, so Take needs nothing typed. */
function renderCard(
  overrides: Partial<Suggestion> = {},
  props: { referencePrice?: number | null; onPassed?: (id: string) => void } = {},
) {
  const { referencePrice = 5230.5, onPassed } = props;
  return renderWithProviders(
    <SuggestionCard
      suggestion={suggestion(overrides)}
      referencePrice={referencePrice}
      {...(onPassed === undefined ? {} : { onPassed })}
    />,
  );
}

function takeButton(): HTMLButtonElement {
  return screen.getByTestId('take-button') as HTMLButtonElement;
}

function passButton(): HTMLButtonElement {
  return screen.getByTestId('pass-button') as HTMLButtonElement;
}

async function click(element: HTMLElement) {
  await act(async () => {
    fireEvent.click(element);
  });
}

beforeEach(() => {
  // Factory mocks are not reset by `restoreMocks`; without this a call count leaks into the next test.
  vi.clearAllMocks();
  takeMock.mockResolvedValue({ ok: true, data: ticket() });
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
      createdAt: ISSUED_AT,
    },
  });
});

// globals are off in this project, so RTL never auto-cleans. Without this the next test renders into a DOM
// that still holds the previous card and every `getBy*` finds two.
afterEach(() => {
  cleanup();
});

describe('SuggestionCard — the fully-specified setup (R-4)', () => {
  it('shows every specified field on the face of the card, nothing behind a click', () => {
    renderCard();

    expect(screen.getByTestId('suggestion-side').textContent).toBe('LONG ES');
    expect(screen.getByTestId('suggestion-timeframe').textContent).toBe('15m');
    expect(screen.getByTestId('suggestion-confidence').textContent).toBe('conf 72%');
    expect(screen.getByTestId('suggestion-entry').textContent).toContain('5230.25');
    expect(screen.getByTestId('suggestion-stop').textContent).toContain('5222.00');
    expect(screen.getByTestId('suggestion-target').textContent).toContain('5248.50');
    expect(screen.getByTestId('suggestion-size').textContent).toContain('2');
    expect(screen.getByTestId('suggestion-size').textContent).toContain('contracts');
    expect(screen.getByTestId('suggestion-rationale').textContent).toContain(
      'reclaimed session VWAP',
    );
    expect(screen.getByTestId('validity-countdown')).toBeTruthy();
    // Dollar risk and reward, and the ratio — the three figures the decision actually turns on.
    expect(screen.getByTestId('suggestion-stop').textContent).toContain('−$412.00');
    expect(screen.getByTestId('suggestion-target').textContent).toContain('+$912.00');
    expect(screen.getByTestId('suggestion-target').textContent).toContain('2.2R');
  });

  it('cites the fired signal with its period AND resolution, not just its name', () => {
    // An indicator without those two is a name, not a citation — and the three are copied at issuance
    // precisely so the citation survives the trigger being edited afterwards.
    renderCard();

    expect(screen.getByTestId('suggestion-citation').textContent).toContain('RSI(14) · 15m');
  });

  it('carries the account mode and the lifecycle state, so the card says what it would trade', () => {
    renderCard({ mode: TradingMode.Live, state: SuggestionState.Stale });

    expect(screen.getByTestId('mode-chip').dataset.mode).toBe('Live');
    expect(screen.getByTestId('suggestion-state').textContent).toBe('Stale');
  });

  it('names the supersede chain rather than hiding that this replaced something', () => {
    renderCard({ version: 3, supersedesId: 's-0' });

    expect(screen.getByTestId('suggestion-meta').textContent).toContain('v3');
    expect(screen.getByTestId('suggestion-meta').textContent).toContain(
      'supersedes an earlier version',
    );
  });

  it('paints a short on the short semantic, never on the brand accent', () => {
    renderCard({ side: OrderSide.Sell, instrument: 'NQ' });

    expect(screen.getByTestId('suggestion-side').dataset.side).toBe('short');
    expect(screen.getByTestId('suggestion-side').textContent).toBe('SHORT NQ');
  });

  it('names a missing dollar figure instead of showing a plausible zero', () => {
    // Null risk/reward means the instrument has no configured contract spec. A guessed tick size is worse
    // than an absent number, so the absence has to be legible as an absence.
    renderCard({ riskUsd: null, rewardUsd: null });

    expect(screen.getByTestId('suggestion-stop').textContent).toContain('no contract spec');
    expect(screen.getByTestId('suggestion-stop').textContent).not.toContain('$0');
  });
});

describe('SuggestionCard — confidence is display-only (R-4)', () => {
  it('leaves a zero-confidence suggestion exactly as actionable as any other', () => {
    // R-4: confidence "never moves size, geometry or the gate, and a zero still issues."
    renderCard({ confidence: 0 });

    expect(screen.getByTestId('suggestion-confidence').textContent).toBe('conf 0%');
    expect(takeButton().disabled).toBe(false);
    expect(passButton().disabled).toBe(false);
  });

  it('paints 0% and 99% identically — no ramp that reads as "act on this"', () => {
    renderCard({ confidence: 0 });
    const low = screen.getByTestId('suggestion-confidence').className;
    cleanup();

    renderCard({ confidence: 99 });
    const high = screen.getByTestId('suggestion-confidence').className;

    expect(high).toBe(low);
  });
});

describe('SuggestionCard — the validity window is a live countdown', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-05T14:00:00.000Z'));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function renderAtWindow(seconds: number) {
    return renderCard({
      expiresAt: new Date(Date.now() + seconds * 1000).toISOString(),
    });
  }

  it('ticks down without any reload or refetch', () => {
    renderAtWindow(120);
    expect(screen.getByTestId('validity-countdown').textContent).toContain('2:00');

    act(() => {
      vi.advanceTimersByTime(60_000);
    });

    expect(screen.getByTestId('validity-countdown').textContent).toContain('1:00');
  });

  it('escalates through warn to critical as the window closes', () => {
    // Expiry is a normal, frequent outcome, so the surface has to make it obvious BEFORE it happens.
    renderAtWindow(180);
    expect(screen.getByTestId('validity-countdown').dataset.urgency).toBe('normal');

    act(() => {
      vi.advanceTimersByTime(61_000);
    });
    expect(screen.getByTestId('validity-countdown').dataset.urgency).toBe('warn');

    act(() => {
      vi.advanceTimersByTime(90_000);
    });
    expect(screen.getByTestId('validity-countdown').dataset.urgency).toBe('critical');
  });

  it('visibly expires in place — the operator never has to reload to learn it died', () => {
    renderAtWindow(30);
    expect(screen.queryByTestId('expired-notice')).toBeNull();
    expect(takeButton().disabled).toBe(false);

    act(() => {
      vi.advanceTimersByTime(31_000);
    });

    expect(screen.getByTestId('validity-countdown').dataset.expired).toBe('true');
    expect(screen.getByTestId('expired-notice')).toBeTruthy();
    expect(screen.getByTestId('suggestion-card').dataset.expired).toBe('true');
  });

  it('stops offering an arm once the window has closed, and says why', () => {
    renderAtWindow(5);

    act(() => {
      vi.advanceTimersByTime(6_000);
    });

    expect(takeButton().disabled).toBe(true);
    expect(screen.getByTestId('take-unavailable').textContent).toContain(
      'validity window has closed',
    );
  });

  it('still offers a pass on an expired suggestion — the note is what R-9 reads', () => {
    // The API accepts a pass on any lifecycle state on purpose: an expired setup's reason is still worth having.
    renderAtWindow(5);

    act(() => {
      vi.advanceTimersByTime(6_000);
    });

    expect(passButton().disabled).toBe(false);
  });
});

describe('SuggestionCard — take arms, it does not send (R-11b)', () => {
  it('stages a ticket and says plainly that nothing was sent', async () => {
    const onArmed = vi.fn();
    renderWithProviders(
      <SuggestionCard suggestion={suggestion()} referencePrice={5230.5} onArmed={onArmed} />,
    );

    await click(takeButton());

    expect(takeMock).toHaveBeenCalledWith('s-1', 5230.5);
    expect(screen.getByTestId('armed-ticket').textContent).toContain('Armed — not sent');
    expect(screen.getByTestId('armed-ticket').textContent).toContain(
      'arming and sending are separate steps',
    );
    expect(onArmed).toHaveBeenCalledWith('s-1', ticket());
  });

  it('surfaces a gate resize rather than implying the suggested size was armed', async () => {
    // The risk gate is authoritative and stages whatever it decided. An operator who thinks 2 went on when 1 did
    // is wrong about their exposure.
    takeMock.mockResolvedValue({
      ok: true,
      data: ticket({ approvedQuantity: 1, outcome: 'Resized' }),
    });
    renderCard();

    await click(takeButton());

    expect(screen.getByTestId('armed-ticket').textContent).toContain('1 of 2 contracts');
    expect(screen.getByTestId('armed-ticket').textContent).toContain('Resized');
  });

  it('never invents the reference price — no price typed, no take attempted', async () => {
    // Defaulting the reference to the suggestion's own entry would make the R-12 drift re-check measure zero
    // drift every time, i.e. delete it.
    renderCard({}, { referencePrice: null });

    expect(screen.getByLabelText('Market price now', { exact: false })).toBeTruthy();
    expect(takeButton().disabled).toBe(true);
    expect(screen.getByTestId('take-unavailable').textContent).toContain('current market price');

    await click(takeButton());
    expect(takeMock).not.toHaveBeenCalled();
  });

  it('takes the operator-entered price, and rejects a non-price outright', async () => {
    renderCard({}, { referencePrice: null });
    const field = screen.getByTestId('reference-price');

    fireEvent.change(field, { target: { value: '52ab' } });
    expect(takeButton().disabled).toBe(true);

    fireEvent.change(field, { target: { value: '5231.75' } });
    await click(takeButton());

    expect(takeMock).toHaveBeenCalledWith('s-1', 5231.75);
  });
});

describe('SuggestionCard — every take refusal renders ITS OWN reason (R-12)', () => {
  it.each([
    [
      'the market drifted',
      422,
      'The market has drifted past this suggestion’s entry tolerance; it can no longer be taken at that level.',
    ],
    [
      'the window closed',
      422,
      "This suggestion's validity window has passed and it can no longer be taken.",
    ],
    [
      'the instrument has no spec',
      422,
      "No contract spec is configured for 'ZZ', so it cannot be sized or taken.",
    ],
    [
      'it was already dispositioned',
      409,
      'This suggestion already has a disposition and cannot be taken.',
    ],
    [
      'a live order already exists',
      409,
      'This suggestion already has a live order; cancel or complete it before taking again.',
    ],
    [
      'the stop is outside the safety floor',
      422,
      "This suggestion's stop is not between its entry and the instrument's catastrophic safety floor, so it cannot be armed safely.",
    ],
  ])('renders the refusal when %s', async (_label, status, reason) => {
    // A refusal is an ORDINARY ANSWER. "Price has drifted" and "the window closed" are different facts and the
    // operator's next move differs, so each must arrive in the server's own words — never collapsed into
    // "something went wrong", and never mapped to a house error code that loses the distinction.
    takeMock.mockResolvedValue({ ok: false, kind: 'refused', status, reason });
    renderCard();

    await click(takeButton());

    const refusal = screen.getByTestId('take-refusal');
    expect(refusal.textContent).toContain(reason);
    expect(refusal.dataset.status).toBe(String(status));
    // Nothing was armed, and nothing pretends otherwise.
    expect(screen.queryByTestId('armed-ticket')).toBeNull();
  });

  it('names the binding risk layer when the gate says which one bound', async () => {
    takeMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'Blocked by the daily governor.',
      layer: 'DailyGovernor',
    });
    renderCard();

    await click(takeButton());

    expect(screen.getByTestId('take-refusal').textContent).toContain('DailyGovernor');
  });

  it('tells a failure apart from a refusal — one is retryable, the other is an answer', async () => {
    takeMock.mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 503,
      error: 'The request failed (503).',
    });
    renderCard();

    await click(takeButton());

    expect(screen.getByTestId('take-failure').textContent).toContain('try again');
    expect(screen.queryByTestId('take-refusal')).toBeNull();
  });
});

describe('SuggestionCard — pass records the disposition (R-4 / R-9)', () => {
  it('passes with the selected reasons and a note', async () => {
    const onPassed = vi.fn();
    renderCard({}, { onPassed });

    await click(passButton());
    fireEvent.click(screen.getByRole('button', { name: 'News risk' }));
    fireEvent.click(screen.getByRole('button', { name: 'Wrong time' }));
    fireEvent.change(screen.getByTestId('pass-note'), {
      target: { value: 'CPI print in 10 min.' },
    });
    await click(screen.getByTestId('pass-save'));

    // NewsRisk (2) | WrongTime (4) — a multi-select, because "news risk AND wrong time" is one answer.
    expect(passMock).toHaveBeenCalledWith('s-1', 6, 'CPI print in 10 min.');
    expect(onPassed).toHaveBeenCalledWith('s-1');
  });

  it('passes with no reason at all — Skip is a complete, neutral decline', async () => {
    const onPassed = vi.fn();
    renderCard({}, { onPassed });

    await click(passButton());
    await click(screen.getByTestId('pass-skip'));

    expect(passMock).toHaveBeenCalledWith('s-1', 0, '');
    expect(onPassed).toHaveBeenCalledWith('s-1');
  });

  it('lets the operator back out of a dialog they opened by mistake', async () => {
    renderCard();

    await click(passButton());
    await click(screen.getByRole('button', { name: 'Cancel' }));

    expect(passMock).not.toHaveBeenCalled();
  });

  it('renders the 409 in its own words when the suggestion was already dispositioned', async () => {
    passMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'This suggestion already has a disposition.',
    });
    const onPassed = vi.fn();
    renderCard({}, { onPassed });

    await click(passButton());
    await click(screen.getByTestId('pass-skip'));

    expect(screen.getByTestId('pass-refusal').textContent).toContain(
      'This suggestion already has a disposition.',
    );
    // Nothing was recorded, so the card must not be reported as passed.
    expect(onPassed).not.toHaveBeenCalled();
  });
});
