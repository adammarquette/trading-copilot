import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ConditionalCrossDirection,
  OrderSide,
  OrderType,
  armOrder,
  cancelConditionalOrder,
  cancelOrder,
  createConditionalOrder,
  editStagedOrder,
  sendAsIsOrder,
  takeStagedOrder,
} from '../api/orders';
import { TradingMode } from '../api/accounts';
import { DefaultEntryAction, getRiskProfile } from '../api/risk';
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
  cancelConditionalOrder: vi.fn(),
  editStagedOrder: vi.fn(),
  sendAsIsOrder: vi.fn(),
}));

// The roster seam. `useOptionalAccounts` is deliberate: outside a provider it is null, and the ticket must then
// FAIL CLOSED to plain arm-then-send rather than throw — the fast path is a convenience, never a requirement.
const { useOptionalAccountsMock } = vi.hoisted(() => ({ useOptionalAccountsMock: vi.fn() }));
vi.mock('../accounts/AccountProvider', () => ({ useOptionalAccounts: useOptionalAccountsMock }));

vi.mock('../api/risk', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/risk')>()),
  getRiskProfile: vi.fn(),
}));

/**
 * Puts one account on the roster as the ticket's own. `tradeableHere` is the server's per-request answer to
 * whether THIS environment may trade it, so it is passed explicitly rather than derived: an `Undeclared` account
 * is never tradeable anywhere, and a live one is tradeable only in production.
 */
function roster(mode: TradingMode, tradeableHere = true) {
  const account = { id: 'a1', mode, name: 'Account 1', tradeableHere };
  useOptionalAccountsMock.mockReturnValue({
    status: 'ready',
    accounts: [account],
    activeAccount: account,
  });
}

/** The declared profile, carrying only the preference this surface reads. */
function profile(defaultEntryAction: DefaultEntryAction) {
  vi.mocked(getRiskProfile).mockResolvedValue({
    ok: true,
    data: { accountId: 'a1', defaultEntryAction } as never,
  });
}

const sendAsIs = vi.mocked(sendAsIsOrder);
const edit = vi.mocked(editStagedOrder);
const arm = vi.mocked(armOrder);
const take = vi.mocked(takeStagedOrder);
const cancel = vi.mocked(cancelOrder);
const createConditional = vi.mocked(createConditionalOrder);
const cancelConditional = vi.mocked(cancelConditionalOrder);

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
  // No roster and no declared profile by default: the ticket falls back to plain arm → review → send, which is
  // what every case below that is not about the split button expects to find.
  useOptionalAccountsMock.mockReturnValue(null);
  vi.mocked(getRiskProfile).mockResolvedValue({ ok: true, data: null });
  sendAsIs.mockResolvedValue({
    ok: true,
    data: {
      outcome: 'Allowed',
      orderId: 'o9',
      venueOrderKey: 'v9',
      approvedQuantity: 5,
      bindingLayer: null,
      reason: 'Within every layer.',
      advisories: [],
    },
  });
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
  cancelConditional.mockResolvedValue({ ok: true, data: undefined });
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

describe('OrderTicket — edit an armed order in place (gh#828)', () => {
  /** Opens the edit form on an armed ticket and sets a new size. */
  async function editSize(quantity: string) {
    await click(/^edit$/i);
    await act(async () => {
      fireEvent.change(screen.getByLabelText(/quantity/i), { target: { value: quantity } });
    });
  }

  it('offers the edit step only once there is a staged order to edit', async () => {
    await renderTicket();
    expect(screen.queryByRole('button', { name: /^edit$/i })).toBeNull();

    await click(/arm/i);

    expect(screen.getByRole('button', { name: /^edit$/i })).toBeTruthy();
  });

  it('re-gates the edit and renders the decision from the EDIT’s response', async () => {
    // The rule this control exists to preserve (ADR-0007): what transmits is the size the gate approved on the
    // EDITED row. Carrying the arm's decision forward would show an approval that no longer describes the order.
    await renderTicket();
    await click(/arm/i);
    edit.mockResolvedValue(
      staged({ outcome: 'Resized', approvedQuantity: 2, reason: 'Per-trade risk allows 2.' }),
    );

    await editSize('4');
    await act(async () => {
      fireEvent.change(screen.getByLabelText(/entry/i), { target: { value: '5001' } });
    });
    await click(/apply/i);

    // The whole proposal travels — the server rebuilds the row from it, so an omitted field is not "unchanged".
    // Both edited fields go, and the safety stop rides along untouched: R-5's floor is not this form's to move.
    expect(edit).toHaveBeenCalledWith(
      'o1',
      expect.objectContaining({
        quantity: 4,
        entry: 5_001,
        safetyStop: 4_990,
        referencePrice: 5_000,
      }),
    );
    const decision = screen.getByTestId('gate-decision').textContent ?? '';
    expect(decision).toContain('Per-trade risk allows 2.');
    expect(decision).toContain('You asked for 4');
  });

  it('measures the resize against the EDITED size, never the size originally armed', async () => {
    // The subtle half: after an edit, "what you asked for" is the edited number. Comparing the gate's answer to
    // the ORIGINAL proposal would report a resize that did not happen — and a false resize on this surface
    // teaches the operator to ignore the real one.
    await renderTicket();
    await click(/arm/i);
    edit.mockResolvedValue(
      staged({ outcome: 'Allowed', approvedQuantity: 2, reason: 'Within every layer.' }),
    );

    await editSize('2');
    await click(/apply/i);

    const decision = screen.getByTestId('gate-decision').textContent ?? '';
    expect(decision).toContain('Sending');
    expect(decision).not.toContain('You asked for');
  });

  it('keeps Apply disabled and flags the field when the quantity is not a whole number (gh#895)', async () => {
    // This is the first free-text quantity field in the ticket. A fractional contract count fails ASP.NET model
    // binding before EditStagedOrderAsync even runs — a bare 400 the operator cannot read — so the form fails fast
    // with the reason instead. R-16 re-gates server-side regardless; this is client-side courtesy, not enforcement.
    await renderTicket();
    await click(/arm/i);
    await editSize('2.5');

    expect((screen.getByRole('button', { name: /apply/i }) as HTMLButtonElement).disabled).toBe(
      true,
    );
    expect(screen.getByText(/whole number of contracts/i)).toBeTruthy();

    await click(/apply/i); // disabled — a no-op
    expect(edit).not.toHaveBeenCalled();
  });

  it('keeps Apply disabled and flags the field when the entry price is not positive (gh#895)', async () => {
    await renderTicket();
    await click(/arm/i);
    await click(/^edit$/i);
    await act(async () => {
      fireEvent.change(screen.getByLabelText(/entry/i), { target: { value: '0' } });
    });

    expect((screen.getByRole('button', { name: /apply/i }) as HTMLButtonElement).disabled).toBe(
      true,
    );
    expect(screen.getByText(/price greater than zero/i)).toBeTruthy();

    await click(/apply/i);
    expect(edit).not.toHaveBeenCalled();
  });

  it('offers no send while an edit is open, so an unapplied change cannot be transmitted', async () => {
    // A typed-but-unapplied size is not what the server holds. Leaving Send live beside it would transmit the
    // PRE-edit staged row while the operator is looking at the number they just typed.
    await renderTicket();
    await click(/arm/i);
    expect(screen.getByRole('button', { name: /^send$/i })).toBeTruthy();

    await click(/^edit$/i);

    expect(screen.queryByRole('button', { name: /^send$/i })).toBeNull();
  });

  it('keeps the pre-edit decision when the edit is REFUSED — nothing changed server-side', async () => {
    // A refused edit leaves the staged row exactly as armed, so the decision on screen must stay the armed one.
    // Replacing or clearing it would misreport what a subsequent send would transmit.
    await renderTicket();
    await click(/arm/i);
    edit.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'Only a staged order can be edited — this one has left staging.',
    });

    await editSize('9');
    await click(/apply/i);

    expect(screen.getByTestId('order-ticket').textContent).toContain('left staging');
    expect(screen.getByTestId('gate-decision').textContent).toContain('Within every layer.');
  });

  it('does not edit twice when Apply is clicked again before the first resolves', async () => {
    await renderTicket();
    await click(/arm/i);
    let settle!: (value: ReturnType<typeof staged>) => void;
    edit.mockReturnValue(
      new Promise((resolve) => {
        settle = resolve;
      }),
    );

    await editSize('3');
    await click(/apply/i);
    await click(/apply/i);

    expect(edit).toHaveBeenCalledTimes(1);
    await act(async () => {
      settle(staged({ approvedQuantity: 3 }));
    });
  });
});

describe('OrderTicket — the default-entry-action split button (gh#828, gh#218)', () => {
  /** Opens the split button's menu. */
  async function openMenu() {
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /entry options/i }));
    });
  }

  async function chooseFromMenu(name: RegExp) {
    await act(async () => {
      fireEvent.click(screen.getByRole('menuitem', { name }));
    });
  }

  it('falls back to plain Arm when the account cannot be resolved — no menu at all', async () => {
    // Fail closed. Without the roster the ticket cannot tell whether this account may default to the one-action
    // send, and a menu whose entries it cannot vouch for is worse than no menu.
    await renderTicket();

    expect(screen.getByRole('button', { name: /^arm$/i })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /entry options/i })).toBeNull();
  });

  it('withholds the fast path entirely for an UNDECLARED account — it produces no orders anywhere', async () => {
    // `TradingMode.Undeclared` is the enum's zero, and an undeclared account is refused everywhere, production
    // included. A roster entry that says "tradeable nowhere" is precisely the one that does NOT vouch for the
    // account, so the split button must not appear at all — no menu, and no reachable one-click transmit.
    roster(TradingMode.Undeclared, false);
    profile(DefaultEntryAction.SendAsIs);

    await renderTicket();

    expect(screen.getByRole('button', { name: /^arm$/i })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /entry options/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /send as-is/i })).toBeNull();
    expect(sendAsIs).not.toHaveBeenCalled();
  });

  it('withholds it where THIS environment may not trade the account (R-14), rather than offering a control that must fail', async () => {
    // A live account outside production is refused by the server on every order path. Offering the fast path for
    // it would be a control that always fails — the same reasoning that keeps TrailingStop off this ticket.
    roster(TradingMode.Live, false);

    await renderTicket();

    expect(screen.queryByRole('button', { name: /entry options/i })).toBeNull();
  });

  it('keeps Approve & arm primary when no profile is declared, offering the fast path in the menu', async () => {
    // `ApproveAndArm` is the enum's zero on purpose: an unset or never-written profile resolves to review-first,
    // never to the one-action send.
    roster(TradingMode.Practice);

    await renderTicket();

    expect(screen.getByRole('button', { name: /^arm$/i })).toBeTruthy();
    await openMenu();
    expect(screen.getByRole('menuitem', { name: /send as-is/i })).toBeTruthy();
  });

  it('makes the operator’s declared default the primary action on a practice account', async () => {
    roster(TradingMode.Practice);
    profile(DefaultEntryAction.SendAsIs);

    await renderTicket();

    expect(screen.getByRole('button', { name: /send as-is/i })).toBeTruthy();
    await openMenu();
    expect(screen.getByRole('menuitem', { name: /approve & arm/i })).toBeTruthy();
  });

  it('DEMOTES a SendAsIs default on a live account — defaulting to it is practice-only (gh#218)', async () => {
    // The preference can only be SET on a practice account, but an account's mode can change under a stored one.
    // Letting it stay the primary click would turn "skip the review" into the default on real money, which is
    // exactly what the practice-only rule exists to prevent. It stays reachable, deliberately, from the menu.
    roster(TradingMode.Live);
    profile(DefaultEntryAction.SendAsIs);

    await renderTicket();

    expect(screen.getByRole('button', { name: /^arm$/i })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /send as-is/i })).toBeNull();
  });

  it('sends in ONE action when the fast path is chosen, staging nothing', async () => {
    roster(TradingMode.Practice);

    await renderTicket();
    await openMenu();
    await chooseFromMenu(/send as-is/i);

    expect(sendAsIs).toHaveBeenCalledWith(
      'a1',
      expect.objectContaining({ quantity: 5, safetyStop: 4_990, type: OrderType.Market }),
    );
    expect(arm).not.toHaveBeenCalled();
    expect(screen.getByTestId('order-ticket').textContent?.toLowerCase()).toContain('sent');
  });

  it('renders a refused fast-path send — it skips the review, never the gate', async () => {
    roster(TradingMode.Practice);
    sendAsIs.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'The daily governor leaves room for nothing.',
    });

    await renderTicket();
    await openMenu();
    await chooseFromMenu(/send as-is/i);

    expect(screen.getByTestId('order-ticket').textContent).toContain('daily governor');
    expect(screen.queryByTestId('gate-decision')).toBeNull();
  });

  it('does not send twice when the fast path is chosen again before the first resolves', async () => {
    // A send-as-is IS a transmission, and transmission is not idempotent (ProjectX ADR-0002) — so this needs the
    // same synchronous guard as Send, not merely a disabled control.
    roster(TradingMode.Practice);
    profile(DefaultEntryAction.SendAsIs);
    let settle!: () => void;
    sendAsIs.mockReturnValue(
      new Promise((resolve) => {
        settle = () =>
          resolve({
            ok: true,
            data: {
              outcome: 'Allowed',
              orderId: 'o9',
              venueOrderKey: 'v9',
              approvedQuantity: 5,
              bindingLayer: null,
              reason: 'ok',
              advisories: [],
            },
          });
      }),
    );

    await renderTicket();
    await click(/send as-is/i);
    await click(/send as-is/i);

    expect(sendAsIs).toHaveBeenCalledTimes(1);
    await act(async () => {
      settle();
    });
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

    // The datetime-local wall-clock is converted to a UTC instant the same way the component does, so this is
    // timezone-robust (a bare date substring would shift in positive-offset zones).
    expect(createConditional.mock.calls[0][1].expiresAt).toBe(
      new Date('2026-08-13T00:00').toISOString(),
    );
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

  it('locks the mode while an arm is in flight, so a mid-round-trip toggle cannot orphan the staged order', async () => {
    // The blocker the adversarial review found: staged/conditional are only set when a request RESOLVES, so if the
    // mode could flip during the in-flight window, the arm's result would land in the conditional branch — the
    // staged order rendering with no Send/Cancel and no way back. The checkbox must lock on `pending` too.
    let release: (value: Awaited<ReturnType<typeof armOrder>>) => void = () => {};
    arm.mockReturnValue(
      new Promise((resolve) => {
        release = resolve;
      }),
    );

    await renderTicket();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /arm/i }));
    });

    expect(
      (screen.getByRole('checkbox', { name: /send when conditions met/i }) as HTMLInputElement)
        .disabled,
    ).toBe(true);

    await act(async () => {
      release(staged());
    });
  });

  it('locks the mode while a conditional create is in flight, so no immediate order stacks on it', async () => {
    // The reverse of the same blocker: untick mid-create and the create's result would land with the Arm button
    // live over a pending conditional — an immediate order on top of the on-trigger one, a genuine double entry.
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
    });

    expect(
      (screen.getByRole('checkbox', { name: /send when conditions met/i }) as HTMLInputElement)
        .disabled,
    ).toBe(true);

    await act(async () => {
      release(pendingConditional());
    });
  });

  it('will not send a non-numeric cancel band — it fails closed rather than silently dropping it', async () => {
    // A NaN cancel band would JSON-serialize to null, silently discarding the operator's stale-cancel intent. The
    // create control stays disabled until the band is a real number or cleared, so it is never lost in flight.
    await renderTicket();
    await armTheTrigger('5010');
    await act(async () => {
      fireEvent.change(screen.getByLabelText(/cancel band/i), { target: { value: '49o0' } });
    });

    expect(
      (screen.getByRole('button', { name: /send on trigger/i }) as HTMLButtonElement).disabled,
    ).toBe(true);

    // Clearing the bad value re-enables the create — the trigger itself is still fireable.
    await act(async () => {
      fireEvent.change(screen.getByLabelText(/cancel band/i), { target: { value: '' } });
    });
    expect(
      (screen.getByRole('button', { name: /send on trigger/i }) as HTMLButtonElement).disabled,
    ).toBe(false);
  });

  it('withdraws a pending conditional by its own id, returning to the trigger form', async () => {
    // A pending conditional will place a real order when it fires (R-12), so the operator must be able to pull it
    // back before then. Withdrawing it deletes the server-side row (nothing rests at the venue) and clears the
    // pending panel; the on-trigger form returns so a new one can be composed.
    await renderTicket();
    await armTheTrigger('5010');
    await click(/send on trigger/i);
    expect(screen.getByTestId('conditional-pending')).toBeTruthy();

    await click(/withdraw/i);

    expect(cancelConditional).toHaveBeenCalledWith('c1');
    expect(screen.queryByTestId('conditional-pending')).toBeNull();
    expect(screen.getByLabelText(/trigger price/i)).toBeTruthy();
  });

  it('keeps the pending conditional on screen when the withdrawal is refused', async () => {
    // A mid-fire conditional cannot be withdrawn (it is reconciled against venue truth instead). The refusal is
    // shown and the pending panel stays — the conditional is still live, so the surface must not imply it is gone.
    cancelConditional.mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 409,
      error: 'Only a pending conditional can be cancelled — this one is Firing.',
    });

    await renderTicket();
    await armTheTrigger('5010');
    await click(/send on trigger/i);

    await click(/withdraw/i);

    expect(screen.getByTestId('order-ticket').textContent?.toLowerCase()).toContain(
      'only a pending conditional',
    );
    expect(screen.getByTestId('conditional-pending')).toBeTruthy();
  });

  it('does not withdraw twice when Withdraw is clicked again before the first resolves', async () => {
    // Deleting is not idempotent from the surface's view: a second click while the network is slow must not fire a
    // second DELETE. The same inFlight guard the arm/send/create paths carry.
    let release: (value: Awaited<ReturnType<typeof cancelConditionalOrder>>) => void = () => {};
    cancelConditional.mockReturnValue(
      new Promise((resolve) => {
        release = resolve;
      }),
    );

    await renderTicket();
    await armTheTrigger('5010');
    await click(/send on trigger/i);

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /withdraw/i }));
      fireEvent.click(screen.getByRole('button', { name: /withdraw/i }));
    });

    expect(cancelConditional).toHaveBeenCalledTimes(1);

    await act(async () => {
      release({ ok: true, data: undefined });
    });
  });
});
