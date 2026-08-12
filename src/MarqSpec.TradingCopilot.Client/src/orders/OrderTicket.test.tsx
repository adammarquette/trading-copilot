import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { OrderSide, OrderType, armOrder, cancelOrder, takeStagedOrder } from '../api/orders';
import { RiskLayer } from './gateDecision';
import { OrderTicket } from './OrderTicket';

vi.mock('../api/orders', async (importOriginal) => ({
  // The enums and SELECTABLE_ORDER_TYPES stay REAL -- the TrailingStop exclusion is a tested property of that
  // list, and stubbing it would let the ticket offer a type the send path refuses.
  ...(await importOriginal<typeof import('../api/orders')>()),
  armOrder: vi.fn(),
  takeStagedOrder: vi.fn(),
  cancelOrder: vi.fn(),
}));

const arm = vi.mocked(armOrder);
const take = vi.mocked(takeStagedOrder);
const cancel = vi.mocked(cancelOrder);

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

  it('reports what was actually sent once it goes', async () => {
    await renderTicket();
    await click(/arm/i);
    await click(/^send$/i);

    expect(screen.getByTestId('order-ticket').textContent?.toLowerCase()).toContain('sent');
  });
});
