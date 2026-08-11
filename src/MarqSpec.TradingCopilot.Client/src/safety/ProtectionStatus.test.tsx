import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { type ProtectionState, getProtectionState } from '../api/protection';
import type { RealtimeContextValue } from '../realtime/RealtimeProvider';
import { useOptionalRealtime } from '../realtime/RealtimeProvider';
import type { RealtimeEvent } from '../realtime/messages';
import { ProtectionStatus } from './ProtectionStatus';

vi.mock('../api/protection', async (importOriginal) => ({
  // isProtectionEvent stays REAL -- it is the tested filter, and stubbing it would let this suite pass while the
  // component re-read on every market quote (or on nothing at all).
  ...(await importOriginal<typeof import('../api/protection')>()),
  getProtectionState: vi.fn(),
}));

vi.mock('../realtime/RealtimeProvider', () => ({ useOptionalRealtime: vi.fn() }));

const read = vi.mocked(getProtectionState);
const realtime = vi.mocked(useOptionalRealtime);

const PROTECTED: ProtectionState = {
  degraded: false,
  orphanedStops: 0,
  nativeSafetyStopRemainsFloor: true,
};
const DEGRADED: ProtectionState = {
  degraded: true,
  orphanedStops: 2,
  nativeSafetyStopRemainsFloor: true,
};

/** Captures the component's event/resync subscriptions so a test can fire them like the socket would. */
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
  } as unknown as RealtimeContextValue);

  return handlers;
}

function event(type: string): RealtimeEvent {
  return { sequence: 1, type, occurredAt: '2026-08-11T12:00:00Z', payload: '{}' };
}

async function renderStatus() {
  const view = render(<ProtectionStatus />);
  await act(async () => {});
  return view;
}

function indicator() {
  return screen.getByTestId('protection-status');
}

function shown() {
  return indicator().textContent ?? '';
}

beforeEach(() => {
  read.mockResolvedValue({ ok: true, data: PROTECTED });
  wireRealtime();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('ProtectionStatus', () => {
  it('reads the state on mount rather than waiting for an event', async () => {
    // The whole reason the read exists: a page loaded while protection is already degraded gets no replay,
    // because a fresh client sends no `?after=` cursor. Waiting for an event would render as protected.
    read.mockResolvedValue({ ok: true, data: DEGRADED });

    await renderStatus();

    expect(read).toHaveBeenCalledTimes(1);
    expect(shown().toLowerCase()).toContain('degraded');
  });

  it('says protection is degraded, never that the operator is unprotected', async () => {
    // R-19, and the distinction the alert turns on. "Unprotected" would be false and would invite a panic close.
    read.mockResolvedValue({ ok: true, data: DEGRADED });

    await renderStatus();

    expect(shown().toLowerCase()).not.toContain('unprotected');
  });

  it('names what still protects the position', async () => {
    read.mockResolvedValue({ ok: true, data: DEGRADED });

    await renderStatus();

    expect(indicator().getAttribute('aria-description')?.toLowerCase()).toContain('safety stop');
  });

  it('shows nothing alarming while protection is intact', async () => {
    await renderStatus();

    expect(shown().toLowerCase()).not.toContain('degraded');
  });

  it('re-reads when the orphan transition is broadcast', async () => {
    const handlers = wireRealtime();
    await renderStatus();
    read.mockResolvedValue({ ok: true, data: DEGRADED });

    await act(async () => {
      handlers.event.forEach((handler) => handler(event('protection.orphaned'), false));
    });

    expect(read).toHaveBeenCalledTimes(2);
    expect(shown().toLowerCase()).toContain('degraded');
  });

  it('re-reads on a REPLAYED transition too, so a reconnect that missed the drop still learns of it', async () => {
    // A historical event is not rendered as a live banner -- it triggers a re-read, and the READ is what is
    // rendered. That is what makes the replay path safe without any is-this-live reasoning in the surface.
    const handlers = wireRealtime();
    await renderStatus();
    read.mockResolvedValue({ ok: true, data: DEGRADED });

    await act(async () => {
      handlers.event.forEach((handler) => handler(event('protection.orphaned'), true));
    });

    expect(shown().toLowerCase()).toContain('degraded');
  });

  it('clears when a restore is broadcast and the server says protection is whole', async () => {
    const handlers = wireRealtime();
    read.mockResolvedValue({ ok: true, data: DEGRADED });
    await renderStatus();
    read.mockResolvedValue({ ok: true, data: PROTECTED });

    await act(async () => {
      handlers.event.forEach((handler) => handler(event('protection.restored'), false));
    });

    expect(shown().toLowerCase()).not.toContain('degraded');
  });

  it('stays degraded when a restore left some stops un-rearmed', async () => {
    // The acceptance criterion that is easiest to get wrong: `protection.restored` fires when SOME stops
    // re-armed. Clearing on the event itself would drop the alert while stops are still orphaned. The re-read is
    // what decides, so a server still reporting orphans keeps the alert up.
    const handlers = wireRealtime();
    read.mockResolvedValue({ ok: true, data: DEGRADED });
    await renderStatus();
    read.mockResolvedValue({ ok: true, data: { ...DEGRADED, orphanedStops: 1 } });

    await act(async () => {
      handlers.event.forEach((handler) => handler(event('protection.restored'), false));
    });

    expect(shown().toLowerCase()).toContain('degraded');
  });

  it('does not re-read on unrelated safety-strip traffic', async () => {
    const handlers = wireRealtime();
    await renderStatus();

    await act(async () => {
      handlers.event.forEach((handler) => handler(event('market.quote'), false));
      handlers.event.forEach((handler) => handler(event('killswitch.engaged'), false));
    });

    expect(read).toHaveBeenCalledTimes(1);
  });

  it('re-reads on a resync, which is the gap path telling it to re-fetch over REST', async () => {
    const handlers = wireRealtime();
    await renderStatus();
    read.mockResolvedValue({ ok: true, data: DEGRADED });

    await act(async () => {
      handlers.resync.forEach((handler) => handler());
    });

    expect(read).toHaveBeenCalledTimes(2);
    expect(shown().toLowerCase()).toContain('degraded');
  });

  it('keeps showing degraded when a re-read fails, rather than reverting to protected', async () => {
    // Failing toward the safe reading. A transient 503 must not silently downgrade a live warning.
    const handlers = wireRealtime();
    read.mockResolvedValue({ ok: true, data: DEGRADED });
    await renderStatus();
    read.mockResolvedValue({ ok: false, kind: 'failed', status: 503, error: 'unavailable' });

    await act(async () => {
      handlers.resync.forEach((handler) => handler());
    });

    expect(shown().toLowerCase()).toContain('degraded');
  });

  it('renders without a realtime provider, so the shell can mount it before the socket exists', async () => {
    realtime.mockReturnValue(null);
    read.mockResolvedValue({ ok: true, data: DEGRADED });

    await renderStatus();

    expect(shown().toLowerCase()).toContain('degraded');
  });
});
