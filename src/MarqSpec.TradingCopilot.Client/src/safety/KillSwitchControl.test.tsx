import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  KillSwitchMode,
  type KillSwitchState,
  disengageKillSwitch,
  engageKillSwitch,
  getKillSwitch,
} from '../api/killSwitch';
import type { RealtimeContextValue } from '../realtime/RealtimeProvider';
import { useOptionalRealtime } from '../realtime/RealtimeProvider';
import type { RealtimeEvent } from '../realtime/messages';
import { KillSwitchControl } from './KillSwitchControl';

vi.mock('../api/killSwitch', async (importOriginal) => ({
  // isKillSwitchEvent stays REAL — it is the tested filter; stubbing it would let this suite pass while the control
  // re-read on every market quote (or on nothing at all).
  ...(await importOriginal<typeof import('../api/killSwitch')>()),
  getKillSwitch: vi.fn(),
  engageKillSwitch: vi.fn(),
  disengageKillSwitch: vi.fn(),
}));

vi.mock('../realtime/RealtimeProvider', () => ({ useOptionalRealtime: vi.fn() }));

const readState = vi.mocked(getKillSwitch);
const engage = vi.mocked(engageKillSwitch);
const disengage = vi.mocked(disengageKillSwitch);
const realtime = vi.mocked(useOptionalRealtime);

/** Captures the control's event/resync subscriptions so a test can fire them like the socket would. */
function wireRealtime() {
  const handlers: {
    event: ((event: RealtimeEvent, historical: boolean) => void)[];
    resync: (() => void)[];
  } = { event: [], resync: [] };

  realtime.mockReturnValue({
    connectionState: 'live',
    onEvent: (handler: (event: RealtimeEvent, historical: boolean) => void) => {
      handlers.event.push(handler);
      return () => {};
    },
    onResync: (handler: () => void) => {
      handlers.resync.push(handler);
      return () => {};
    },
    onOrderState: () => () => {},
    onFill: () => () => {},
    onSuggestion: () => () => {},
  } as unknown as RealtimeContextValue);

  return handlers;
}

function killEvent(type: string): RealtimeEvent {
  return { sequence: 1, type, occurredAt: '2026-08-11T12:00:00Z', payload: '{}' };
}

const DISENGAGED: KillSwitchState = {
  engaged: false,
  mode: 'FlattenAll',
  engagedAt: null,
  reason: null,
};
const ENGAGED: KillSwitchState = {
  engaged: true,
  mode: 'HaltOnly',
  engagedAt: '2026-08-10T13:00:00Z',
  reason: 'stepping away',
};

async function renderControl() {
  const view = render(<KillSwitchControl />);
  await act(async () => {});
  return view;
}

function control() {
  return screen.getByTestId('kill-switch');
}

async function click(element: Element) {
  await act(async () => {
    fireEvent.click(element);
  });
}

beforeEach(() => {
  readState.mockResolvedValue({ ok: true, data: DISENGAGED });
  engage.mockResolvedValue({
    ok: true,
    data: { mode: 'FlattenAll', cancelledOrders: 0, flattenedPositions: 0 },
  });
  disengage.mockResolvedValue({ ok: true, data: undefined });
  wireRealtime();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('KillSwitchControl', () => {
  it('takes its state from the server rather than assuming a fresh session is disengaged', async () => {
    // gh#189's rehydration, which is exactly the property that must not be hidden: the switch outlives the
    // process. A client that rendered "armed" on load because the page is new would tell the operator trading
    // is enabled while the server is still refusing every send.
    readState.mockResolvedValue({ ok: true, data: ENGAGED });

    await renderControl();

    expect(control().textContent?.toLowerCase()).toContain('engaged');
  });

  it('does not engage on a plain click', async () => {
    // R-11 hold-to-confirm. A panic control adjacent to ordinary chrome must not fire from a stray or mistaken
    // click -- engaging cancels working orders and (in the default mode) flattens open positions.
    await renderControl();

    await click(screen.getByRole('button', { name: /kill switch/i }));

    expect(engage).not.toHaveBeenCalled();
  });

  it('explains what engaging does NOT stop before asking for confirmation', async () => {
    // The card's acceptance criterion, and a genuine safety misconception: an operator who believes engaging
    // strands their open positions may hesitate at the exact moment they should not. It stops new risk; it does
    // not stop protection -- auto-flatten's close and stop promotion do not route through the send path.
    await renderControl();

    await click(screen.getByRole('button', { name: /kill switch/i }));

    const explanation = screen.getByRole('dialog').textContent?.toLowerCase() ?? '';
    expect(explanation).toContain('auto-flatten');
    expect(explanation).toContain('safety stop');
  });

  it('engages with the confirmed gesture once the operator confirms', async () => {
    await renderControl();

    await click(screen.getByRole('button', { name: /kill switch/i }));
    await click(screen.getByRole('button', { name: /engage/i }));

    expect(engage).toHaveBeenCalledWith(KillSwitchMode.FlattenAll, null);
  });

  it('engages halt-only when that mode is chosen', async () => {
    // The two modes differ in whether open positions are closed. Choosing halt-only and getting a flatten would
    // close positions the operator deliberately left on their native safety stops.
    await renderControl();

    await click(screen.getByRole('button', { name: /kill switch/i }));
    await click(screen.getByRole('radio', { name: /halt only/i }));
    await click(screen.getByRole('button', { name: /engage/i }));

    expect(engage).toHaveBeenCalledWith(KillSwitchMode.HaltOnly, null);
  });

  it('reads the new state back after engaging rather than assuming it', async () => {
    // What the switch now IS is the server's answer, not an inference from a 200. So the stub behaves like the
    // server: disengaged on load, engaged once engaged. A control that flipped optimistically would show
    // "ENGAGED" even where the engage partially failed.
    readState
      .mockResolvedValueOnce({ ok: true, data: DISENGAGED })
      .mockResolvedValue({ ok: true, data: ENGAGED });

    await renderControl();

    await click(screen.getByRole('button', { name: /kill switch/i }));
    await click(screen.getByRole('button', { name: /engage/i }));

    expect(readState).toHaveBeenCalledTimes(2);
    expect(control().textContent?.toLowerCase()).toContain('engaged');
  });

  it('disengages on request', async () => {
    readState.mockResolvedValue({ ok: true, data: ENGAGED });

    await renderControl();
    await click(screen.getByRole('button', { name: /disengage/i }));

    expect(disengage).toHaveBeenCalled();
  });

  it('keeps showing engaged when a disengage is refused', async () => {
    // Failing toward the safe state. If the server would not re-enable trading, a control that optimistically
    // flipped to "armed" would tell the operator sends will go through when they will not.
    readState.mockResolvedValue({ ok: true, data: ENGAGED });
    disengage.mockResolvedValue({ ok: false, kind: 'failed', status: 500, error: 'nope' });

    await renderControl();
    await click(screen.getByRole('button', { name: /disengage/i }));

    expect(control().textContent?.toLowerCase()).toContain('engaged');
  });

  it('re-reads when another window broadcasts an engage (gh#985)', async () => {
    // The cross-window staleness this card fixes: this window loaded disengaged; the operator engages the kill
    // switch in a pop-out (gh#651), which broadcasts killswitch.engaged. Without the subscription this window keeps
    // showing KILL as if trading were still enabled.
    const handlers = wireRealtime();
    await renderControl();
    expect(control().textContent?.toLowerCase()).not.toContain('engaged');
    readState.mockResolvedValue({ ok: true, data: ENGAGED });

    await act(async () => {
      handlers.event.forEach((handler) => handler(killEvent('killswitch.engaged'), false));
    });

    expect(readState).toHaveBeenCalledTimes(2);
    expect(control().textContent?.toLowerCase()).toContain('engaged');
  });

  it('re-reads when another window broadcasts a disengage, dropping the stale ENGAGED', async () => {
    // The dangerous direction: this window shows ENGAGED, the operator disengages elsewhere. A window left on
    // ENGAGED tells the operator trading is halted when it is not.
    readState.mockResolvedValue({ ok: true, data: ENGAGED });
    const handlers = wireRealtime();
    await renderControl();
    expect(control().textContent?.toLowerCase()).toContain('engaged');
    readState.mockResolvedValue({ ok: true, data: DISENGAGED });

    await act(async () => {
      handlers.event.forEach((handler) => handler(killEvent('killswitch.disengaged'), false));
    });

    expect(control().textContent?.toLowerCase()).not.toContain('engaged');
  });

  it('re-reads on a resync, the reconnect / retention-gap re-fetch', async () => {
    const handlers = wireRealtime();
    await renderControl();
    readState.mockResolvedValue({ ok: true, data: ENGAGED });

    await act(async () => {
      handlers.resync.forEach((handler) => handler());
    });

    expect(control().textContent?.toLowerCase()).toContain('engaged');
  });

  it('ignores unrelated traffic on the shared safety-strip channel', async () => {
    const handlers = wireRealtime();
    await renderControl();

    await act(async () => {
      handlers.event.forEach((handler) => handler(killEvent('protection.orphaned'), false));
      handlers.event.forEach((handler) => handler(killEvent('market.quote'), false));
    });

    expect(readState).toHaveBeenCalledTimes(1); // just the mount read
  });

  it('ignores a superseded read that lands after a newer one', async () => {
    // ProtectionStatus's #777 lesson, here on the kill switch: the mount read (engaged) resolves LATE, after a
    // broadcast-driven read (disengaged) already landed. Ordering is by generation, so the stale ENGAGED must not
    // overwrite the newer DISENGAGED — otherwise the strip claims trading is halted when it is not.
    let resolveMount: (value: { ok: true; data: KillSwitchState }) => void = () => {};
    readState.mockReturnValueOnce(
      new Promise((resolve) => {
        resolveMount = resolve;
      }),
    );
    const handlers = wireRealtime();
    render(<KillSwitchControl />);
    await act(async () => {});

    let resolveEvent: (value: { ok: true; data: KillSwitchState }) => void = () => {};
    readState.mockReturnValueOnce(
      new Promise((resolve) => {
        resolveEvent = resolve;
      }),
    );
    await act(async () => {
      handlers.event.forEach((handler) => handler(killEvent('killswitch.disengaged'), false));
    });

    // The newer (broadcast) read lands first: disengaged.
    await act(async () => {
      resolveEvent({ ok: true, data: DISENGAGED });
    });
    expect(control().textContent?.toLowerCase()).not.toContain('engaged');

    // The older (mount) read resolves late with the stale ENGAGED. It must not win.
    await act(async () => {
      resolveMount({ ok: true, data: ENGAGED });
    });
    expect(control().textContent?.toLowerCase()).not.toContain('engaged');
  });

  it('renders without a realtime provider, so the shell can mount it before the socket exists', async () => {
    realtime.mockReturnValue(null);
    readState.mockResolvedValue({ ok: true, data: ENGAGED });

    await renderControl();

    expect(control().textContent?.toLowerCase()).toContain('engaged');
  });
});
