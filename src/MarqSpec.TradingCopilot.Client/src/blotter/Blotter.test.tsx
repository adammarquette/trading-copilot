import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  type BlotterPosition,
  type BlotterRestingOrder,
  type VenueView,
  exitPosition,
  getPositions,
  getRestingOrders,
} from '../api/blotter';
import { cancelOrder, type RepriceResult, repriceOrder } from '../api/orders';
import type { RealtimeContextValue } from '../realtime/RealtimeProvider';
import { useOptionalRealtime } from '../realtime/RealtimeProvider';
import { Blotter } from './Blotter';

vi.mock('../api/blotter', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/blotter')>()),
  getPositions: vi.fn(),
  getRestingOrders: vi.fn(),
  exitPosition: vi.fn(),
}));

vi.mock('../realtime/RealtimeProvider', () => ({ useOptionalRealtime: vi.fn() }));

vi.mock('../api/orders', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/orders')>()),
  cancelOrder: vi.fn(),
  repriceOrder: vi.fn(),
}));

const positions = vi.mocked(getPositions);
const restingOrders = vi.mocked(getRestingOrders);
const realtime = vi.mocked(useOptionalRealtime);
const cancel = vi.mocked(cancelOrder);
const exit = vi.mocked(exitPosition);
const reprice = vi.mocked(repriceOrder);

// A benign reprice result — a full-size approval, no downsize (gh#969); tests that need a downsize override it.
const REPRICED: RepriceResult = {
  id: 'o1',
  status: 'Working',
  entryPrice: 4991,
  workingStopPrice: 4980,
  size: 1,
  requestedSize: null,
  outcome: 'Allowed',
};

const LONG: BlotterPosition = {
  contract: 'CON.F.US.MES.U26',
  netQuantity: 2,
  averagePrice: 5_000,
  isFlat: false,
};

const PROTECTIVE: BlotterRestingOrder = {
  venueOrderKey: 'v1',
  // A journaled protective stop the app placed — it has an Order row, so it is actionable through /orders/{id}.
  orderId: 'ord-1',
  contract: 'CON.F.US.MES.U26',
  stopPrice: 4_990,
  limitPrice: null,
  size: 2,
  isProtective: true,
};

/** A venue-spawned bracket leg: no journaled Order row, so `orderId` is null and it is not actionable via /orders/{id}. */
const UNJOURNALED_LEG: BlotterRestingOrder = {
  venueOrderKey: 'v2',
  orderId: null,
  contract: 'CON.F.US.MES.U26',
  stopPrice: 4_980,
  limitPrice: null,
  size: 1,
  isProtective: true,
};

/**
 * A resting order carrying a HIDDEN working stop the operator can move (gh#865). Long: the entry is above the
 * safety floor, so the band is safety(4980) < working(4990) < entry(5000). The four platform-held fields are
 * present as a whole — that is what makes the Move stop control appear.
 */
const HIDDEN_LONG: BlotterRestingOrder = {
  venueOrderKey: 'v3',
  orderId: 'ord-2',
  contract: 'CON.F.US.MES.U26',
  stopPrice: null,
  limitPrice: 5_000,
  size: 2,
  isProtective: false,
  workingStopPrice: 4_990,
  stopStaging: 'Hidden',
  safetyStopPrice: 4_980,
  entryPrice: 5_000,
};

/** The short mirror: the entry is BELOW the safety floor, so the band inverts — entry(5000) < working(5010) < safety(5020). */
const HIDDEN_SHORT: BlotterRestingOrder = {
  ...HIDDEN_LONG,
  venueOrderKey: 'v4',
  workingStopPrice: 5_010,
  safetyStopPrice: 5_020,
  entryPrice: 5_000,
};

/** A working stop already promoted to a NATIVE venue order — it rests at the venue, so it is not locally movable and offers no Move stop. */
const NATIVE_STOP: BlotterRestingOrder = { ...HIDDEN_LONG, stopStaging: 'Native' };

function live<T>(...items: T[]): VenueView<T> {
  return { basis: 'live', items };
}

/** Captures the component's realtime subscriptions so a test can fire them like the socket would. */
function wireRealtime() {
  const handlers: { orderState: (() => void)[]; fill: (() => void)[]; resync: (() => void)[] } = {
    orderState: [],
    fill: [],
    resync: [],
  };

  realtime.mockReturnValue({
    connectionState: 'live',
    onEvent: () => () => {},
    onOrderState: (handler: () => void) => {
      handlers.orderState.push(handler);
      return () => {};
    },
    onFill: (handler: () => void) => {
      handlers.fill.push(handler);
      return () => {};
    },
    onResync: (handler: () => void) => {
      handlers.resync.push(handler);
      return () => {};
    },
  } as unknown as RealtimeContextValue);

  return handlers;
}

function panel() {
  return screen.getByTestId('blotter');
}

function shown() {
  return panel().textContent ?? '';
}

async function renderBlotter() {
  const view = render(<Blotter accountId="a1" />);
  await act(async () => {});
  return view;
}

beforeEach(() => {
  positions.mockResolvedValue({ ok: true, data: live(LONG) });
  restingOrders.mockResolvedValue({ ok: true, data: live(PROTECTIVE) });
  wireRealtime();
  cancel.mockResolvedValue({ ok: true, data: undefined });
  exit.mockResolvedValue({ ok: true, data: { outcome: 'Flat', netQuantity: 0 } });
  reprice.mockResolvedValue({ ok: true, data: REPRICED });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('Blotter', () => {
  it('reads both positions and resting orders on mount', async () => {
    await renderBlotter();

    expect(positions).toHaveBeenCalledWith('a1');
    expect(restingOrders).toHaveBeenCalledWith('a1');
  });

  it('shows the position from venue truth', async () => {
    await renderBlotter();

    expect(shown()).toContain('CON.F.US.MES.U26');
    expect(shown()).toContain('2');
  });

  it('renders an unknown basis as UNKNOWN, never as flat or empty', async () => {
    // The failure this whole card exists to prevent. An outage must not read as "no positions" -- "flat" reads
    // as safe, and the operator would believe they have nothing at risk.
    positions.mockResolvedValue({ ok: true, data: { basis: 'unknown' } });

    await renderBlotter();

    // Asserted on the CLAIM, not on the word: the copy deliberately says "not a statement that you are flat",
    // so banning the substring would forbid the very sentence that makes the state unambiguous.
    expect(panel().querySelector('[data-testid="positions-unknown"]')).toBeTruthy();
    expect(shown().toLowerCase()).not.toContain('no open positions');
  });

  it('renders a genuinely flat account as flat, distinctly from unknown', async () => {
    // The other half: an empty LIVE view is real information and must not be hedged into "unknown", or the
    // operator learns to ignore the warning that matters.
    positions.mockResolvedValue({ ok: true, data: live<BlotterPosition>() });

    await renderBlotter();

    expect(shown().toLowerCase()).toContain('no open positions');
    expect(shown().toLowerCase()).not.toContain('unknown');
  });

  it('marks a settlement view as a re-mark, not as live', async () => {
    positions.mockResolvedValue({ ok: true, data: { basis: 'settlement', items: [LONG] } });

    await renderBlotter();

    expect(shown().toLowerCase()).toContain('settlement');
  });

  it('says a position is protected when a protective order rests on its contract', async () => {
    // gh#370's question, answered from the reads the card already lists: is a stop actually standing AT THE VENUE?
    await renderBlotter();

    expect(panel().querySelector('[data-protection="protected"]')).toBeTruthy();
  });

  it('says a position is UNPROTECTED when no protective order rests on its contract', async () => {
    // The dangerous direction, and the one ProtectionMonitorHost pages P1 on. A non-protective resting order on
    // the same contract must not count as protection.
    restingOrders.mockResolvedValue({
      ok: true,
      data: live({ ...PROTECTIVE, isProtective: false, stopPrice: null }),
    });

    await renderBlotter();

    expect(panel().querySelector('[data-protection="unprotected"]')).toBeTruthy();
  });

  it('does not claim a position is unprotected when the orders view is unknown', async () => {
    // Absence of evidence is not evidence of absence: if the resting-order read is unknown, protection state is
    // unknown too -- asserting "unprotected" would raise a false alarm, and asserting "protected" would hide a
    // real one.
    restingOrders.mockResolvedValue({ ok: true, data: { basis: 'unknown' } });

    await renderBlotter();

    expect(panel().querySelector('[data-protection="unknown"]')).toBeTruthy();
    expect(panel().querySelector('[data-protection="unprotected"]')).toBeNull();
  });

  it('shows the resting order size the read carries', async () => {
    await renderBlotter();

    expect(panel().querySelector('[data-testid="resting-orders"]')?.textContent).toContain('2');
  });

  it('re-reads when an order state is pushed', async () => {
    const handlers = wireRealtime();
    await renderBlotter();

    await act(async () => {
      handlers.orderState.forEach((handler) => handler());
    });

    expect(positions).toHaveBeenCalledTimes(2);
    expect(restingOrders).toHaveBeenCalledTimes(2);
  });

  it('re-reads when a fill is pushed', async () => {
    const handlers = wireRealtime();
    await renderBlotter();

    await act(async () => {
      handlers.fill.forEach((handler) => handler());
    });

    expect(positions).toHaveBeenCalledTimes(2);
  });

  it('re-reads on a resync, which is the reconnect telling it to re-fetch', async () => {
    const handlers = wireRealtime();
    await renderBlotter();

    await act(async () => {
      handlers.resync.forEach((handler) => handler());
    });

    expect(positions).toHaveBeenCalledTimes(2);
  });

  it('renders a failed read as unavailable, not as an empty account', async () => {
    positions.mockResolvedValue({ ok: false, kind: 'failed', status: 503, error: 'down' });

    await renderBlotter();

    expect(shown().toLowerCase()).toContain('unavailable');
    expect(shown().toLowerCase()).not.toContain('no open positions');
  });

  it('ignores a superseded read that lands after a newer one', async () => {
    // The same ordering hazard found on the protection strip and the order ticket: reads overlap here by design
    // (every push triggers one), so an older answer resolving late must not overwrite a newer one -- which on
    // this surface could put a closed position back on screen, or drop a live one.
    let resolveFirst: (value: { ok: true; data: VenueView<BlotterPosition> }) => void = () => {};
    positions.mockReturnValueOnce(
      new Promise((resolve) => {
        resolveFirst = resolve;
      }),
    );

    const handlers = wireRealtime();
    render(<Blotter accountId="a1" />);
    await act(async () => {});

    positions.mockResolvedValue({ ok: true, data: live<BlotterPosition>() }); // newer: now flat
    await act(async () => {
      handlers.fill.forEach((handler) => handler());
    });
    expect(shown().toLowerCase()).toContain('no open positions');

    // The older read lands late, still reporting the open position. It must not win.
    await act(async () => {
      resolveFirst({ ok: true, data: live(LONG) });
    });
    expect(shown().toLowerCase()).toContain('no open positions');
  });

  it('names the specific order before cancelling it', async () => {
    // Acceptance: every destructive control states what it will do to WHICH order before it acts. A generic
    // "are you sure" on a blotter with several resting orders is exactly how the wrong one gets pulled.
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /cancel/i }));
    });

    const confirmation = screen.getByRole('dialog').textContent ?? '';
    expect(confirmation).toContain('CON.F.US.MES.U26');
    expect(confirmation).toContain('2');
    expect(cancel).not.toHaveBeenCalled();
  });

  it('cancels only after the confirmation is accepted', async () => {
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /cancel/i }));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^cancel this order$/i }));
    });

    // The app Order.Id the endpoint routes on ({id:guid}), never the venue key it cannot match (gh#656).
    expect(cancel).toHaveBeenCalledWith('ord-1');
  });

  it('warns that cancelling a PROTECTIVE order removes protection', async () => {
    // The one cancel that is not merely undoing an intent: pulling a protective leg leaves the position exposed,
    // and the operator should be told that rather than discovering it from the protection chip afterwards.
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /cancel/i }));
    });

    expect((screen.getByRole('dialog').textContent ?? '').toLowerCase()).toContain('protective');
  });

  it('re-reads after a cancel, rather than assuming it worked', async () => {
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /cancel/i }));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^cancel this order$/i }));
    });

    expect(positions).toHaveBeenCalledTimes(2);
  });

  it('does not cancel twice when confirmed twice before the first resolves', async () => {
    // The same non-idempotent-transmit hazard as the order ticket: a second click before the first settles
    // issues a second cancel.
    let release: () => void = () => {};
    cancel.mockReturnValue(
      new Promise((resolve) => {
        release = () => resolve({ ok: true, data: undefined });
      }),
    );

    await renderBlotter();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /cancel/i }));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^cancel this order$/i }));
      fireEvent.click(screen.getByRole('button', { name: /^cancel this order$/i }));
    });

    expect(cancel).toHaveBeenCalledTimes(1);
    await act(async () => {
      release();
    });
  });

  it('offers no cancel for an unjournaled leg, since /orders/{id} cannot reach it', async () => {
    // gh#656: a venue-spawned bracket leg has no journaled Order row (ADR-0007), so it has no app Order.Id and
    // DELETE /orders/{id} cannot route to it. Rather than a Cancel that would 404, the blotter states plainly that
    // the leg is managed via its position — the honest, safe rendering, not a control that silently fails.
    restingOrders.mockResolvedValue({ ok: true, data: live(UNJOURNALED_LEG) });

    await renderBlotter();

    expect(screen.queryByRole('button', { name: /cancel/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /reprice/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /move stop/i })).toBeNull();
    expect(screen.getByTestId('leg-not-actionable').textContent?.toLowerCase()).toContain(
      'manage via its position',
    );
    expect(cancel).not.toHaveBeenCalled();
    expect(reprice).not.toHaveBeenCalled();
  });

  it('reprices a resting order by its journaled id, carrying the operator-supplied market price', async () => {
    // Acceptance: reprice a working order. An entry move re-gates against a reference price (R-16); the server has
    // no quote read, so the operator supplies the current market price, and the move targets the app Order.Id.
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /reprice/i }));
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId('new-entry-price'), { target: { value: '4991' } });
      fireEvent.change(screen.getByTestId('reprice-reference'), { target: { value: '4993' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^reprice this order$/i }));
    });

    expect(reprice).toHaveBeenCalledWith('ord-1', { entryPrice: 4991, referencePrice: 4993 });
  });

  it('will not reprice until a market price is supplied — the re-gate needs it', async () => {
    // Fail closed: an entry move without a reference price is refused server-side (R-16), so the action stays
    // disabled here rather than round-tripping to a certain refusal. The new entry is seeded; the market is not.
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /reprice/i }));
    });

    expect(
      (screen.getByRole('button', { name: /^reprice this order$/i }) as HTMLButtonElement).disabled,
    ).toBe(true);

    await act(async () => {
      fireEvent.change(screen.getByTestId('reprice-reference'), { target: { value: '4993' } });
    });
    expect(
      (screen.getByRole('button', { name: /^reprice this order$/i }) as HTMLButtonElement).disabled,
    ).toBe(false);
    expect(reprice).not.toHaveBeenCalled();
  });

  it('keeps the sheet up and shows the gate’s refusal, rather than reporting a move', async () => {
    // A reprice re-gates and CAN be refused (a breach, a fat-finger band). The order did not move, so the sheet
    // stays and the reason is shown — the same posture the order ticket takes on a refused send.
    reprice.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 422,
      reason: 'would breach the drawdown floor',
    });

    await renderBlotter();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /reprice/i }));
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId('new-entry-price'), { target: { value: '4991' } });
      fireEvent.change(screen.getByTestId('reprice-reference'), { target: { value: '4993' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^reprice this order$/i }));
    });

    expect(screen.getByTestId('reprice-refusal').textContent?.toLowerCase()).toContain(
      'drawdown floor',
    );
    expect(screen.getByTestId('new-entry-price')).toBeTruthy(); // the sheet is still open
  });

  it('re-reads after a successful reprice, rather than assuming the new price took', async () => {
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /reprice/i }));
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId('new-entry-price'), { target: { value: '4991' } });
      fireEvent.change(screen.getByTestId('reprice-reference'), { target: { value: '4993' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^reprice this order$/i }));
    });

    expect(positions).toHaveBeenCalledTimes(2);
  });

  it('surfaces a gate-approved downsize rather than applying it silently (gh#969)', async () => {
    // gh#292: a resize can be honoured at a SMALLER quantity than asked. The 200 body echoes it; the operator must
    // see the approved size, not be left believing they got the size they requested.
    reprice.mockResolvedValue({
      ok: true,
      data: { ...REPRICED, size: 2, requestedSize: 3, outcome: 'Resized' },
    });

    await renderBlotter();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /reprice/i }));
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId('new-entry-price'), { target: { value: '4991' } });
      fireEvent.change(screen.getByTestId('reprice-reference'), { target: { value: '4993' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^reprice this order$/i }));
    });

    // The full phrase, not just the digits: a swapped "approved 3 ... requested 2" must fail, the plural must agree
    // with the approved 2, and the CONTRACT is named so a lingering notice cannot be misread as another order's
    // (the operator reads this to learn their size was trimmed, so it has to be exact).
    const notice = screen.getByTestId('reprice-resized').textContent ?? '';
    expect(notice).toContain('CON.F.US.MES.U26: gate approved 2 contracts — you requested 3.');
  });

  it('does not reprice twice when confirmed twice before the first resolves', async () => {
    // The same non-idempotent-transmit hazard as the cancel: a reprice reaches the venue, so a second click before
    // the first settles must not issue a second move.
    let release: () => void = () => {};
    reprice.mockReturnValue(
      new Promise((resolve) => {
        release = () => resolve({ ok: true, data: REPRICED });
      }),
    );

    await renderBlotter();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /reprice/i }));
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId('new-entry-price'), { target: { value: '4991' } });
      fireEvent.change(screen.getByTestId('reprice-reference'), { target: { value: '4993' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^reprice this order$/i }));
      fireEvent.click(screen.getByRole('button', { name: /^reprice this order$/i }));
    });

    expect(reprice).toHaveBeenCalledTimes(1);
    await act(async () => {
      release();
    });
  });

  it('offers a Move stop control only when the working stop is Hidden', async () => {
    // gh#865: only a Hidden working stop is locally movable. Native (a real venue order) and Orphaned (re-arms on
    // reconnect) are not, and an order with no stop plan has no hidden stop at all — none may offer the control.
    restingOrders.mockResolvedValue({ ok: true, data: live(HIDDEN_LONG) });

    await renderBlotter();

    expect(screen.getByRole('button', { name: /^move stop$/i })).toBeTruthy();
  });

  it('offers no Move stop control for a Native stop — it rests at the venue, not locally', async () => {
    restingOrders.mockResolvedValue({ ok: true, data: live(NATIVE_STOP) });

    await renderBlotter();

    expect(screen.queryByRole('button', { name: /^move stop$/i })).toBeNull();
  });

  it('offers no Move stop control for an order with no stop plan', async () => {
    // The default PROTECTIVE fixture carries no staging fields — a plain protective stop, not a hidden working stop.
    await renderBlotter();

    expect(screen.queryByRole('button', { name: /^move stop$/i })).toBeNull();
  });

  it('rejects a working stop outside the safety→entry band (long) and accepts one inside it', async () => {
    // The band is strict at both ends: onto the safety stop removes it, onto the entry is degenerate — both are
    // refused. Client validation is UX; the server re-validates the same band fail-closed. Long: 4980 < w < 5000.
    restingOrders.mockResolvedValue({ ok: true, data: live(HIDDEN_LONG) });
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move stop$/i }));
    });
    const confirm = () =>
      screen.getByRole('button', { name: /^move this stop$/i }) as HTMLButtonElement;

    for (const outOfBand of ['5000', '5001', '4980', '4979']) {
      await act(async () => {
        fireEvent.change(screen.getByTestId('new-working-stop'), { target: { value: outOfBand } });
      });
      expect(confirm().disabled).toBe(true);
      expect(screen.getByTestId('move-stop-band')).toBeTruthy(); // the inline out-of-band reason
    }

    await act(async () => {
      fireEvent.change(screen.getByTestId('new-working-stop'), { target: { value: '4995' } });
    });
    expect(confirm().disabled).toBe(false);
    expect(screen.queryByTestId('move-stop-band')).toBeNull();
    expect(reprice).not.toHaveBeenCalled();
  });

  it('rejects a working stop outside the band (short) and accepts one inside it', async () => {
    // Side is not on the venue-truth record, so direction is inferred from band geometry. Short: the entry is below
    // the safety floor, so the band inverts — 5000 < w < 5020 — and the same strict bounds apply.
    restingOrders.mockResolvedValue({ ok: true, data: live(HIDDEN_SHORT) });
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move stop$/i }));
    });
    const confirm = () =>
      screen.getByRole('button', { name: /^move this stop$/i }) as HTMLButtonElement;

    for (const outOfBand of ['5000', '4999', '5020', '5021']) {
      await act(async () => {
        fireEvent.change(screen.getByTestId('new-working-stop'), { target: { value: outOfBand } });
      });
      expect(confirm().disabled).toBe(true);
    }

    await act(async () => {
      fireEvent.change(screen.getByTestId('new-working-stop'), { target: { value: '5010' } });
    });
    expect(confirm().disabled).toBe(false);
  });

  it('moves the hidden stop by its journaled id, sending only the working stop', async () => {
    // Acceptance: the move targets the app Order.Id, and a hidden re-stage is a LOCAL write — it needs no reference
    // price (unlike an entry reprice), so the payload carries the working stop and nothing else.
    restingOrders.mockResolvedValue({ ok: true, data: live(HIDDEN_LONG) });
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move stop$/i }));
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId('new-working-stop'), { target: { value: '4995' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move this stop$/i }));
    });

    // Exact-object match: no entryPrice, no referencePrice — naming a field would be a request to move it.
    expect(reprice).toHaveBeenCalledWith('ord-2', { workingStopPrice: 4995 });
  });

  it('keeps the sheet up and shows the server’s band refusal, rather than reporting a move', async () => {
    reprice.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 422,
      reason: 'the move must keep the safety → working → entry ordering',
    });
    restingOrders.mockResolvedValue({ ok: true, data: live(HIDDEN_LONG) });
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move stop$/i }));
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId('new-working-stop'), { target: { value: '4995' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move this stop$/i }));
    });

    expect(screen.getByTestId('move-stop-refusal').textContent?.toLowerCase()).toContain(
      'ordering',
    );
    expect(screen.getByTestId('new-working-stop')).toBeTruthy(); // the sheet is still open
  });

  it('surfaces a lost-race refusal when the stop promoted to a native venue order mid-edit', async () => {
    // The UI offered the control because the read said Hidden, but the stop promoted before the move landed. The
    // server refuses (409) and the reason reaches the operator — proving the server is the real guard, not the UI
    // gate. Nothing unprotected results: the move simply did not happen.
    reprice.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'the working stop is now a native venue order and cannot be re-staged here',
    });
    restingOrders.mockResolvedValue({ ok: true, data: live(HIDDEN_LONG) });
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move stop$/i }));
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId('new-working-stop'), { target: { value: '4995' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move this stop$/i }));
    });

    expect(screen.getByTestId('move-stop-refusal').textContent?.toLowerCase()).toContain(
      'native venue order',
    );
  });

  it('re-reads after a successful move, rather than assuming the stop moved', async () => {
    restingOrders.mockResolvedValue({ ok: true, data: live(HIDDEN_LONG) });
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move stop$/i }));
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId('new-working-stop'), { target: { value: '4995' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move this stop$/i }));
    });

    expect(positions).toHaveBeenCalledTimes(2);
  });

  it('does not move the stop twice when confirmed twice before the first resolves', async () => {
    // The same non-idempotent-transmit hazard as reprice/cancel: a re-stage reaches the venue, so a second click
    // before the first settles must not issue a second move.
    let release: () => void = () => {};
    reprice.mockReturnValue(
      new Promise((resolve) => {
        release = () => resolve({ ok: true, data: REPRICED });
      }),
    );
    restingOrders.mockResolvedValue({ ok: true, data: live(HIDDEN_LONG) });
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move stop$/i }));
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId('new-working-stop'), { target: { value: '4995' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move this stop$/i }));
      fireEvent.click(screen.getByRole('button', { name: /^move this stop$/i }));
    });

    expect(reprice).toHaveBeenCalledTimes(1);
    await act(async () => {
      release();
    });
  });

  it('names the specific order and its current stop before moving it', async () => {
    // Acceptance: every modify control states WHICH order and its current terms before acting — a blotter carries
    // several at once, and moving the wrong stop is the same class of mistake as pulling the wrong order.
    restingOrders.mockResolvedValue({ ok: true, data: live(HIDDEN_LONG) });
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^move stop$/i }));
    });

    const sheet = screen.getByRole('dialog').textContent ?? '';
    expect(sheet).toContain('CON.F.US.MES.U26');
    expect(sheet).toContain('4990'); // the current working stop, so the operator sees what they are moving from
    expect(reprice).not.toHaveBeenCalled();
  });

  it('names the position, its size and its protection before exiting it', async () => {
    // The acceptance criterion, on the most destructive control here: exiting closes real exposure at market, so
    // the operator is told WHICH position and WHAT it is before it happens -- never a bare "are you sure".
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /exit/i }));
    });

    const confirmation = screen.getByRole('dialog').textContent ?? '';
    expect(confirmation).toContain('CON.F.US.MES.U26');
    expect(confirmation).toContain('2');
    expect(confirmation.toLowerCase()).toContain('market');
    expect(exit).not.toHaveBeenCalled();
  });

  it('exits only after the confirmation is accepted', async () => {
    await renderBlotter();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /exit/i }));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^exit this position$/i }));
    });

    expect(exit).toHaveBeenCalledWith('a1', 'CON.F.US.MES.U26');
  });

  it('reports a still-open exit rather than presenting it as done', async () => {
    // The distinction the endpoint draws, carried through to the operator: the close was accepted but the venue
    // still reports exposure. Rendering that as success would stop them watching a live position.
    exit.mockResolvedValue({ ok: false, kind: 'failed', status: 409, error: 'StillOpen' });

    await renderBlotter();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /exit/i }));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^exit this position$/i }));
    });

    // Rendered INSIDE the dialog the operator is already looking at. As a sibling it sat behind MUI's backdrop
    // and was invisible until they dismissed the dialog -- so the one message that stops them walking away from
    // a still-live position needed an extra, undocumented step to see (#851 review).
    const dialog = screen.getByRole('dialog').textContent?.toLowerCase() ?? '';
    expect(dialog).toContain('stillopen');
    expect(dialog).toContain('may still be open');
  });

  it('clears a previous exit failure when a different position is opened', async () => {
    // A stale failure left on screen would read as this position's result.
    exit.mockResolvedValue({ ok: false, kind: 'failed', status: 409, error: 'StillOpen' });

    await renderBlotter();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /exit/i }));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^exit this position$/i }));
    });
    expect((screen.getByRole('dialog').textContent ?? '').toLowerCase()).toContain('stillopen');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /keep it/i }));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /exit/i }));
    });

    expect((screen.getByRole('dialog').textContent ?? '').toLowerCase()).not.toContain('stillopen');
  });

  it('re-reads after an exit rather than assuming the position is gone', async () => {
    await renderBlotter();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /exit/i }));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^exit this position$/i }));
    });

    expect(positions).toHaveBeenCalledTimes(2);
  });
});
