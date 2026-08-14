import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { useAccountsMock, useRealtimeMock, getFillsMock } = vi.hoisted(() => ({
  useAccountsMock: vi.fn(),
  useRealtimeMock: vi.fn(),
  getFillsMock: vi.fn(),
}));
vi.mock('../accounts/AccountProvider', () => ({ useAccounts: useAccountsMock }));
vi.mock('../realtime/RealtimeProvider', () => ({ useRealtime: useRealtimeMock }));
vi.mock('../api/fills', () => ({ getFills: getFillsMock }));

import { useFillMarkers } from './useFillMarkers';

let fillHandler: (() => void) | null = null;
let resyncHandler: (() => void) | null = null;

function setRealtime(connectionState: string): void {
  useRealtimeMock.mockReturnValue({
    connectionState,
    onEvent: vi.fn(),
    onOrderState: vi.fn(),
    onFill: (handler: () => void) => {
      fillHandler = handler;
      return vi.fn();
    },
    onResync: (handler: () => void) => {
      resyncHandler = handler;
      return vi.fn();
    },
  });
}

beforeEach(() => {
  vi.clearAllMocks();
  fillHandler = null;
  resyncHandler = null;
  useAccountsMock.mockReturnValue({ status: 'ready', activeAccount: { id: 'acc-1' } });
  setRealtime('live');
  getFillsMock.mockResolvedValue({ ok: true, data: { fills: [] } });
});

afterEach(() => {
  fillHandler = null;
  resyncHandler = null;
});

describe('useFillMarkers', () => {
  it('loads the active account fills scoped to the instrument over a recent window, and maps to marks', async () => {
    getFillsMock.mockResolvedValue({
      ok: true,
      data: {
        fills: [
          {
            venueFillKey: 'f1',
            side: 'Buy',
            price: 5300,
            size: 2,
            fees: 1.25,
            executedAt: '2026-07-28T15:00:00Z',
          },
        ],
      },
    });

    const { result } = renderHook(() => useFillMarkers('ES'));

    await waitFor(() => expect(result.current.fills).toHaveLength(1));
    expect(result.current.fills[0]).toEqual({
      id: 'f1',
      time: Math.floor(Date.parse('2026-07-28T15:00:00Z') / 1000),
      side: 'Buy',
      size: 2,
    });
    expect(result.current.unavailable).toBe(false);
    // Owner-scoped by the active account; instrument-scoped; a non-empty from < to window.
    const [account, instrument, from, to] = getFillsMock.mock.calls[0];
    expect(account).toBe('acc-1');
    expect(instrument).toBe('ES');
    expect(Date.parse(from)).toBeLessThan(Date.parse(to));
  });

  it('has no marks and never loads when there is no ready account', async () => {
    useAccountsMock.mockReturnValue({ status: 'loading' });

    const { result } = renderHook(() => useFillMarkers('ES'));

    await waitFor(() => expect(result.current.fills).toEqual([]));
    expect(getFillsMock).not.toHaveBeenCalled();
  });

  it('refreshes on a fill push and a resync (gh#683 as a signal, not marker data)', async () => {
    renderHook(() => useFillMarkers('ES'));
    await waitFor(() => expect(getFillsMock).toHaveBeenCalledTimes(1));

    act(() => fillHandler?.());
    await waitFor(() => expect(getFillsMock).toHaveBeenCalledTimes(2));

    act(() => resyncHandler?.());
    await waitFor(() => expect(getFillsMock).toHaveBeenCalledTimes(3));
  });

  it('coalesces a burst of fill pushes into a single re-read — the clearTimeout debounce (gh#727)', async () => {
    // Fake timers so the assertion is on the coalescing itself, not a wall-clock / waitFor race: with real timers a
    // broken debounce fires two loads back-to-back, and the transient "2" could be observed by chance either way.
    vi.useFakeTimers();
    try {
      renderHook(() => useFillMarkers('ES'));
      // The mount load calls getFills synchronously; flush its resolved-promise state update under act.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });
      expect(getFillsMock).toHaveBeenCalledTimes(1);

      // Two pushes land inside the same coalesce window with no await between them — a real multi-fill burst.
      act(() => {
        fillHandler?.();
        fillHandler?.();
      });

      // Advance well past the coalesce window (REFRESH_COALESCE_MS = 120) and flush the single coalesced load.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(200);
      });

      // 1 mount load + 1 coalesced refresh = 2, never 3. Dropping the clearTimeout in scheduleRefresh makes this 3.
      expect(getFillsMock).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('flags the markers stale whenever the socket is not live (R-19)', async () => {
    setRealtime('reconnecting');

    const { result } = renderHook(() => useFillMarkers('ES'));

    await waitFor(() => expect(result.current.stale).toBe(true));
  });

  it('flags unavailable when the read is refused / fails — an empty overlay is not "no trades" (R-19)', async () => {
    getFillsMock.mockResolvedValue({ ok: false, kind: 'refused', status: 400, reason: 'too wide' });

    const { result } = renderHook(() => useFillMarkers('ES'));

    await waitFor(() => expect(result.current.unavailable).toBe(true));
    expect(result.current.fills).toEqual([]);
  });
});
