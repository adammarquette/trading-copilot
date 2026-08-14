import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { cancelOrder, takeStagedOrder } from '../api/orders';
import type { StagedTicket } from '../api/suggestions';
import { RiskLayer } from './gateDecision';
import { StagedTicketReview } from './StagedTicketReview';

vi.mock('../api/orders', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/orders')>()),
  takeStagedOrder: vi.fn(),
  cancelOrder: vi.fn(),
}));

const take = vi.mocked(takeStagedOrder);
const cancel = vi.mocked(cancelOrder);

function ticket(overrides: Partial<StagedTicket> = {}): StagedTicket {
  return {
    orderId: 'o1',
    status: 'Staged',
    outcome: 'Allowed',
    approvedQuantity: 5,
    bindingLayer: null,
    reason: 'Within every layer.',
    target: 5020,
    ...overrides,
  };
}

const onDone = vi.fn();

async function click(name: RegExp) {
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name }));
  });
}

function renderReview(props: Partial<Parameters<typeof StagedTicketReview>[0]> = {}) {
  return render(
    <StagedTicketReview
      ticket={ticket()}
      requested={5}
      summary="LONG MES"
      onDone={onDone}
      {...props}
    />,
  );
}

beforeEach(() => {
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

describe('StagedTicketReview', () => {
  it('shows the gate decision for the staged ticket and offers send', () => {
    renderReview();

    expect(screen.getByTestId('gate-decision')).toBeTruthy();
    expect(screen.getByRole('button', { name: /^send$/i })).toBeTruthy();
  });

  it('sends the staged ticket by its id — never re-arming, never re-sending the proposal', async () => {
    // The ticket is already armed server-side (the take staged it). Send transmits THAT row by id; nothing here
    // re-sizes or re-proposes.
    renderReview();
    await click(/^send$/i);

    expect(take).toHaveBeenCalledWith('o1');
  });

  it('reports what was sent and holds the confirmation until the operator acknowledges it', async () => {
    // A real send must not vanish silently. The confirmation stays on screen; only Done returns to the list, so the
    // operator always sees that the order went (and for how much) before the surface goes away.
    renderReview();
    await click(/^send$/i);

    expect(screen.getByTestId('staged-ticket-review').textContent?.toLowerCase()).toContain('sent');
    expect(onDone).not.toHaveBeenCalled(); // not until acknowledged
    expect(screen.queryByRole('button', { name: /^send$/i })).toBeNull(); // no second send

    await click(/done/i);
    expect(onDone).toHaveBeenCalledTimes(1);
  });

  it('makes the resize impossible to miss — both the requested and the approved size are shown', () => {
    renderReview({
      ticket: ticket({
        outcome: 'Resized',
        approvedQuantity: 2,
        bindingLayer: RiskLayer.DailyGovernor,
        reason: 'Room for 2.',
      }),
      requested: 5,
    });

    const panel = screen.getByTestId('gate-decision');
    expect(panel.textContent).toContain('5');
    expect(panel.textContent).toContain('2');
    expect(screen.getByRole('button', { name: /^send$/i })).toBeTruthy();
  });

  it('offers no send when the gate approved nothing — only a way out', () => {
    renderReview({
      ticket: ticket({
        outcome: 'Blocked',
        approvedQuantity: 0,
        bindingLayer: RiskLayer.DrawdownFloor,
        reason: 'No room.',
      }),
      requested: 5,
    });

    expect(screen.getByTestId('gate-decision').getAttribute('data-sendable')).toBe('false');
    expect(screen.queryByRole('button', { name: /^send$/i })).toBeNull();
    expect(screen.getByRole('button', { name: /cancel/i })).toBeTruthy();
  });

  it('cancels the staged ticket by id and finishes', async () => {
    renderReview();
    await click(/cancel/i);

    expect(cancel).toHaveBeenCalledWith('o1');
    expect(onDone).toHaveBeenCalledTimes(1);
  });

  it('keeps the review open with the reason when a send is refused', async () => {
    take.mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 422,
      error: 'gate re-validation failed',
    });

    renderReview();
    await click(/^send$/i);

    expect(screen.getByTestId('staged-ticket-review').textContent?.toLowerCase()).toContain(
      're-validation',
    );
    expect(screen.getByRole('button', { name: /^send$/i })).toBeTruthy();
    expect(onDone).not.toHaveBeenCalled();
  });

  it('keeps the review open and warns when a cancel is refused — a refused cancel can mean the order is still live', async () => {
    // The venue can reject a cancel on an order that has gone Working/Filled, and a Taking race can land here too
    // (409, no terminal status). Closing the panel as if cancelled would tell the operator there is no order when
    // one may be live — the one failure this review step exists to prevent (#838 review).
    cancel.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'the order is working at the venue',
    });

    renderReview();
    await click(/cancel/i);

    const text = screen.getByTestId('staged-ticket-review').textContent?.toLowerCase() ?? '';
    expect(text).toContain('working at the venue'); // the venue's own reason
    expect(text).toContain('may still be live'); // the honesty the swallowed .finally() dropped
    expect(onDone).not.toHaveBeenCalled(); // the review stays open, not closed as if the order were gone
    expect(screen.getByRole('button', { name: /cancel/i })).toBeTruthy(); // and the operator can retry
  });

  it('keeps the review open and warns when a cancel request outright fails', async () => {
    // Belt-and-suspenders for an unexpected throw (the api client normally maps failures to ok:false): still must
    // not report the order gone.
    cancel.mockRejectedValue(new Error('network down'));

    renderReview();
    await click(/cancel/i);

    expect(screen.getByTestId('staged-ticket-review').textContent?.toLowerCase()).toContain(
      'may still be live',
    );
    expect(onDone).not.toHaveBeenCalled();
  });

  it('does not send twice when Send is clicked again before the first resolves', async () => {
    let release: (value: Awaited<ReturnType<typeof takeStagedOrder>>) => void = () => {};
    take.mockReturnValue(
      new Promise((resolve) => {
        release = resolve;
      }),
    );

    renderReview();
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
});
