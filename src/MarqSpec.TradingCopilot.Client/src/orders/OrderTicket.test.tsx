import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ConditionalCrossDirection,
  OrderSide,
  OrderType,
  armOrder,
  cancelOrder,
  createConditionalOrder,
  takeStagedOrder,
} from '../api/orders';
import { RiskLayer } from './gateDecision';
import { OrderTicket } from './OrderTicket';

vi.mock('../api/orders', async (importOriginal) => ({
  // The enums and SELECTABLE_ORDER_TYPES stay REAL -- the TrailingStop exclusion is a tested property of that
  // list, and stubbing it would let the ticket offer a type the send path refuses.
  ...(await importOriginal<typeof import('../api/orders')>()),
  armOrder: vi.fn(),
  takeStagedOrder: vi.fn(),
  cancelOrder: vi.fn(),
  createConditionalOrder: vi.fn(),
}));

const arm = vi.mocked(armOrder);
const take = vi.mocked(takeStagedOrder);
const cancel = vi.mocked(cancelOrder);
const createConditional = vi.mocked(createConditionalOrder);

const PROPOSAL = {
  accountId: 'a1',
  symbol: 'MES',
  tickSize: 0.25,
  pointValue: 5,
  side: OrderSide.Buy,
  quantity: 5,
  entry: 5_000,
  stop: 4_995,
  safetyStop: 4_990,
  referencePrice: 5_000,
  type: OrderType.Market,
};

function staged(overrides: Record<string, unknown> = {}) {
  return {
    ok: true as const,
    data: {
      orderId: 'o1',
      status: 'Staged',
      outcome: 'Allowed',
      approvedQuantity: 5,
      bindingLayer: null,
      reason: 'Within every layer.',
      target: null,
      advisories: [],
      ...overrides,
    },
  };
}

function pendingConditional(overrides: Record<string, unknown> = {}) {
  return {
    ok: true as const,
    data: {
      conditionalOrderId: 'c1',
      status: 'Pending',
      triggerPrice: 5010,
      triggerDirection: 'RisesTo',
      outcome: 'Allowed',
      approvedQuantity: 5,
      bindingLayer: null,
      reason: 'Within every layer at creation.',
      ...overrides,
    },
  };
}

/** Enters conditional mode and sets a fireable trigger (a positive price; direction defaults to Rises to). */
async function armTheTrigger(price = '5010') {
  await act(async () => {
    fireEvent.click(screen.getByRole('checkbox', { name: /send when conditions met/i }));
  });
  await act(async () => {
    fireEvent.change(screen.getByLabelText(/trigger price/i), { target: { value: price } });
  });
}

async function click(name: RegExp) {
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name }));
  });
}

async function renderTicket() {
  const view = render(<OrderTicket proposal={PROPOSAL} />);
  await act(async () => {});
  return view;
}

beforeEach(() => {
  arm.mockResolvedValue(staged());
  take.mockResolvedValue({
    ok: true,
    data: {
      outcome: 'Allowed',
      orderId: 'o1',
      venueOrderKey: 'v1',
      approvedQuantity: 5,
      bindingLayer: null,
      reason: 'ok',
      advisories: [],
    },
  });
  cancel.mockResolvedValue({ ok: true, data: undefined });
  createConditional.mockResolvedValue(pendingConditional());
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('OrderTicket', () => {
  it('offers no send control before the order is armed', async () => {
    // Arm -> review -> send is three steps (R-11), and the review step is the point. A send available from the
    // unarmed ticket would collapse it to one click and make the gate's decision something you skip past.
    await renderTicket();

    expect(screen.queryByRole('button', { name: /^send$/i })).toBeNull();
    expect(take).not.toHaveBeenCalled();
  });

  it('arms without transmitting', async () => {
    await renderTicket();

    await click(/arm/i);

    expect(arm).toHaveBeenCalledTimes(1);
    expect(take).not.toHaveBeenCalled();
  });

  it('shows the gate decision once armed, and only then offers send', async () => {
    await renderTicket();

    await click(/arm/i);

    expect(screen.getByTestId('gate-decision')).toBeTruthy();
    expect(screen.getByRole('button', { name: /^send$/i })).toBeTruthy();
  });

  it('sends the staged order, never re-sending the proposal', async () => {
    // The staged row already carries the gate-approved size. Re-sending the proposal would transmit the REQUESTED
    // quantity and make the gate advisory -- the exact failure ADR-0007 names.
    await renderTicket();

    await click(/arm/i);
    await click(/^send$/i);

    expect(take).toHaveBeenCalledWith('o1');
  });

  it('refuses to offer send when the gate approved nothing', async () => {
    arm.mockResolvedValue(
      staged({
        outcome: 'Blocked',
        approvedQuantity: 0,
        bindingLayer: RiskLayer.DrawdownFloor,
        reason: 'No room.',
      }),
    );

    await renderTicket();
    await click(/arm/i);

    expect(screen.getByTestId('gate-decision').getAttribute('data-sendable')).toBe('false');
    expect(screen.queryByRole('button', { name: /^send$/i })).toBeNull();
  });

  it('still offers send on a resize, at the approved size', async () => {
    // A resize is sendable -- it is the gate doing its job, not a refusal. What must not happen is sending it
    // silently, which the panel prevents by showing both numbers.
    arm.mockResolvedValue(
      staged({
        outcome: 'Resized',
        approvedQuantity: 2,
        bindingLayer: RiskLayer.DailyGovernor,
        reason: 'Room for 2.',
      }),
    );

    await renderTicket();
    await click(/arm/i);

    expect(screen.getByTestId('gate-decision').textContent).toContain('5');
    expect(screen.getByTestId('gate-decision').textContent).toContain('2');
    expect(screen.getByRole('button', { name: /^send$/i })).toBeTruthy();
  });

  it('shows the safety stop on the ticket, always', async () => {
    // "Insured" is the promise the wireframe makes: the catastrophic floor rests at the venue on every order, so
    // the ticket states it rather than leaving the operator to assume it.
    await renderTicket();

    expect(screen.getByTestId('order-ticket').textContent).toContain('4990');
  });

  it('never offers a trailing stop', async () => {
    // The send path refuses TrailingStop outright and the neutral ticket carries no trail distance, so a control
    // offering it would always fail. Absent beats always-failing.
    await renderTicket();

    const types = screen.getAllByRole('option').map((option) => option.textContent ?? '');
    expect(types.join(' ').toLowerCase()).not.toContain('trailing');
  });

  it('surfaces a refused arm rather than pretending it staged', async () => {
    arm.mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 409,
      error: 'account not tradable',
    });

    await renderTicket();
    await click(/arm/i);

    expect(screen.getByTestId('order-ticket').textContent?.toLowerCase()).toContain('not tradable');
    expect(screen.queryByRole('button', { name: /^send$/i })).toBeNull();
  });

  it('keeps the ticket armed when a send is refused, so the operator can see why', async () => {
    // A refused send must not silently discard the staged row: it still exists server-side, and the operator
    // needs the reason in front of them to decide whether to amend or cancel.
    take.mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 422,
      error: 'gate re-validation failed',
    });

    await renderTicket();
    await click(/arm/i);
    await click(/^send$/i);

    expect(screen.getByTestId('order-ticket').textContent?.toLowerCase()).toContain(
      're-validation',
    );
    expect(screen.getByRole('button', { name: /^send$/i })).toBeTruthy();
  });

  it('cancels the staged order and returns to the unarmed ticket', async () => {
    await renderTicket();
    await click(/arm/i);
    await click(/cancel/i);

    expect(cancel).toHaveBeenCalledWith('o1');
    expect(screen.queryByRole('button', { name: /^send$/i })).toBeNull();
  });

  it('does not send twice when Send is clicked again before the first resolves', async () => {
    // #797 review. The worst thing this ticket can do. Order transmission is not idempotent (ProjectX ADR-0002:
    // "a retried timeout can place a second live order"), so a double-click while the network is slow could put
    // the gate-approved size on the account TWICE. `staged` has not changed yet, so without a guard the button is
    // still live and the handler re-enters.
    let release: (value: Awaited<ReturnType<typeof takeStagedOrder>>) => void = () => {};
    take.mockReturnValue(
      new Promise((resolve) => {
        release = resolve;
      }),
    );

    await renderTicket();
    await click(/arm/i);

    // Both clicks land BEFORE the first request settles -- the race the guard exists for.
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^send$/i }));
      fireEvent.click(screen.getByRole('button', { name: /^send$/i }));
    });

    expect(take).toHaveBeenCalledTimes(1);

    await act(async () => {
      release({
        ok: true,
        data: {
          outcome: 'Allowed',
          orderId: 'o1',
          venueOrderKey: 'v1',
          approvedQuantity: 5,
          bindingLayer: null,
          reason: 'ok',
          advisories: [],
        },
      });
    });
  });

  it('does not arm twice when Arm is clicked again before the first resolves', async () => {
    // Milder but still wrong: a second arm stages a SECOND order server-side, and whichever response resolves
    // last silently overwrites `staged` -- orphaning the first staged row where this ticket can neither show nor
    // cancel it.
    let release: (value: Awaited<ReturnType<typeof armOrder>>) => void = () => {};
    arm.mockReturnValue(
      new Promise((resolve) => {
        release = resolve;
      }),
    );

    await renderTicket();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /arm/i }));
      fireEvent.click(screen.getByRole('button', { name: /arm/i }));
    });

    expect(arm).toHaveBeenCalledTimes(1);

    await act(async () => {
      release(staged());
    });
  });

  it('reports what was actually sent once it goes', async () => {
    await renderTicket();
    await click(/arm/i);
    await click(/^send$/i);

    expect(screen.getByTestId('order-ticket').textContent?.toLowerCase()).toContain('sent');
  });
});

describe('OrderTicket — send when conditions met (gh#655)', () => {
  it('hides the trigger until the operator opts into the conditional mode', async () => {
    // "Send now" is the default; the on-trigger inputs appear only when asked for, so the common path stays the
    // three-step arm → review → send.
    await renderTicket();

    expect(screen.queryByLabelText(/trigger price/i)).toBeNull();
    expect(screen.getByRole('button', { name: /arm/i })).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByRole('checkbox', { name: /send when conditions met/i }));
    });

    expect(screen.getByLabelText(/trigger price/i)).toBeTruthy();
    // The immediate-send flow is replaced, not shown alongside — the two are mutually exclusive.
    expect(screen.queryByRole('button', { name: /arm/i })).toBeNull();
  });

  it('creates the conditional from the proposal and the trigger, transmitting nothing', async () => {
    await renderTicket();
    await armTheTrigger('5010');

    await click(/send on trigger/i);

    expect(createConditional).toHaveBeenCalledTimes(1);
    const [account, request_] = createConditional.mock.calls[0];
    expect(account).toBe('a1');
    expect(request_.order.symbol).toBe('MES');
    expect(request_.triggerPrice).toBe(5010);
    expect(request_.triggerDirection).toBe(ConditionalCrossDirection.RisesTo); // the default direction
    // Nothing about a conditional reaches the venue now — no arm, no take.
    expect(arm).not.toHaveBeenCalled();
    expect(take).not.toHaveBeenCalled();
  });

  it('shows the pending state as off-book and re-gated at fire time — never as armed or sent', async () => {
    await renderTicket();
    await armTheTrigger('5010');
    await click(/send on trigger/i);

    const panel = screen.getByTestId('conditional-pending');
    const text = panel.textContent ?? '';
    expect(text).toContain('rises to 5010'); // the trigger, phrased from the server's direction name
    expect(text.toLowerCase()).toContain('off the book'); // held local — no order anticipation
    expect(text.toLowerCase()).toContain('not a placed order'); // the honesty line: it is not armed
    expect(text.toLowerCase()).toContain('re-checked'); // the gate re-runs at fire time (R-12)
    // It is a pending conditional, not a staged order and not a transmitted one.
    expect(screen.queryByTestId('gate-decision')).toBeNull();
    expect(screen.queryByRole('button', { name: /^send$/i })).toBeNull();
  });

  it('passes the chosen direction — a pullback falls to its trigger', async () => {
    await renderTicket();
    await armTheTrigger('4990');
    await act(async () => {
      fireEvent.change(screen.getByLabelText(/direction/i), {
        target: { value: String(ConditionalCrossDirection.FallsTo) },
      });
    });

    await click(/send on trigger/i);

    expect(createConditional.mock.calls[0][1].triggerDirection).toBe(
      ConditionalCrossDirection.FallsTo,
    );
  });

  it('carries an optional expiry through to the request when the operator sets one', async () => {
    await renderTicket();
    await armTheTrigger('5010');
    await act(async () => {
      fireEvent.change(screen.getByLabelText(/expiry/i), {
        target: { value: '2026-08-13T00:00' },
      });
    });

    await click(/send on trigger/i);

    expect(createConditional.mock.calls[0][1].expiresAt).toContain('2026-08-13');
  });

  it('will not create a conditional that could never fire — no trigger price, no send', async () => {
    await renderTicket();
    await act(async () => {
      fireEvent.click(screen.getByRole('checkbox', { name: /send when conditions met/i }));
    });

    // A missing (or non-positive) trigger price is not fireable — the control is disabled, not merely ignored.
    const button = screen.getByRole('button', { name: /send on trigger/i }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);

    await act(async () => {
      fireEvent.change(screen.getByLabelText(/trigger price/i), { target: { value: '5010' } });
    });
    expect(
      (screen.getByRole('button', { name: /send on trigger/i }) as HTMLButtonElement).disabled,
    ).toBe(false);
  });

  it('surfaces a refused conditional create rather than a pending order', async () => {
    createConditional.mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 409,
      error: 'this environment will not trade that account mode',
    });

    await renderTicket();
    await armTheTrigger('5010');
    await click(/send on trigger/i);

    expect(screen.getByTestId('order-ticket').textContent?.toLowerCase()).toContain(
      'will not trade',
    );
    expect(screen.queryByTestId('conditional-pending')).toBeNull();
  });

  it('does not create twice when Send on trigger is clicked again before the first resolves', async () => {
    // The same non-idempotence guard the arm/send paths carry: a double-click while the network is slow must not
    // create two pending conditionals.
    let release: (value: Awaited<ReturnType<typeof createConditionalOrder>>) => void = () => {};
    createConditional.mockReturnValue(
      new Promise((resolve) => {
        release = resolve;
      }),
    );

    await renderTicket();
    await armTheTrigger('5010');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /send on trigger/i }));
      fireEvent.click(screen.getByRole('button', { name: /send on trigger/i }));
    });

    expect(createConditional).toHaveBeenCalledTimes(1);

    await act(async () => {
      release(pendingConditional());
    });
  });
});
