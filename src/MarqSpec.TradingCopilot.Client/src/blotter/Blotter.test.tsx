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
import { cancelOrder, repriceOrder } from '../api/orders';
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
  reprice.mockResolvedValue({ ok: true, data: undefined });
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

  it('does not reprice twice when confirmed twice before the first resolves', async () => {
    // The same non-idempotent-transmit hazard as the cancel: a reprice reaches the venue, so a second click before
    // the first settles must not issue a second move.
    let release: () => void = () => {};
    reprice.mockReturnValue(
      new Promise((resolve) => {
        release = () => resolve({ ok: true, data: undefined });
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

    expect(shown().toLowerCase()).toContain('stillopen');
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
