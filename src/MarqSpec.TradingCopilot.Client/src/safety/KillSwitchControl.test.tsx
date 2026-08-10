import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  KillSwitchMode,
  type KillSwitchState,
  disengageKillSwitch,
  engageKillSwitch,
  getKillSwitch,
} from '../api/killSwitch';
import { KillSwitchControl } from './KillSwitchControl';

vi.mock('../api/killSwitch', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/killSwitch')>()),
  getKillSwitch: vi.fn(),
  engageKillSwitch: vi.fn(),
  disengageKillSwitch: vi.fn(),
}));

const readState = vi.mocked(getKillSwitch);
const engage = vi.mocked(engageKillSwitch);
const disengage = vi.mocked(disengageKillSwitch);

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
});
