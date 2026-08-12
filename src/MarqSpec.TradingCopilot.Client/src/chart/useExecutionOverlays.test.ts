import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { useAccountsMock, useRealtimeMock, getWorkingOrdersMock, getPositionsMock } = vi.hoisted(
  () => ({
    useAccountsMock: vi.fn(),
    useRealtimeMock: vi.fn(),
    getWorkingOrdersMock: vi.fn(),
    getPositionsMock: vi.fn(),
  }),
);
vi.mock('../accounts/AccountProvider', () => ({ useAccounts: useAccountsMock }));
vi.mock('../realtime/RealtimeProvider', () => ({ useRealtime: useRealtimeMock }));
vi.mock('../api/execution', () => ({
  getWorkingOrders: getWorkingOrdersMock,
  getPositions: getPositionsMock,
}));

import { useExecutionOverlays } from './useExecutionOverlays';

let orderStateHandler: (() => void) | null = null;
let fillHandler: (() => void) | null = null;
let resyncHandler: (() => void) | null = null;

function setRealtime(connectionState: string): void {
  useRealtimeMock.mockReturnValue({
    connectionState,
    onEvent: vi.fn(),
    onOrderState: (handler: () => void) => {
      orderStateHandler = handler;
      return vi.fn();
    },
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
  orderStateHandler = null;
  fillHandler = null;
  resyncHandler = null;
  useAccountsMock.mockReturnValue({ status: 'ready', activeAccount: { id: 'acc-1' } });
  setRealtime('live');
  getWorkingOrdersMock.mockResolvedValue({ ok: true, data: { markBasis: 'Live', orders: [] } });
  getPositionsMock.mockResolvedValue({ ok: true, data: { markBasis: 'Live', positions: [] } });
});

afterEach(() => {
  orderStateHandler = null;
  fillHandler = null;
  resyncHandler = null;
});

describe('useExecutionOverlays', () => {
  it('loads the active account scoped to the instrument, and maps orders + the net position', async () => {
    getWorkingOrdersMock.mockResolvedValue({
      ok: true,
      data: {
        markBasis: 'Live',
        orders: [
          {
            venueOrderKey: 'o1',
            contract: 'C',
            stopPrice: 5290,
            limitPrice: null,
            size: 2,
            isProtective: true,
          },
          {
            venueOrderKey: 'o2',
            contract: 'C',
            stopPrice: null,
            limitPrice: 5320,
            size: 2,
            isProtective: false,
          },
        ],
      },
    });
    getPositionsMock.mockResolvedValue({
      ok: true,
      data: {
        markBasis: 'Live',
        positions: [{ contract: 'C', netQuantity: 2, averagePrice: 5300, isFlat: false }],
      },
    });

    const { result } = renderHook(() => useExecutionOverlays('ES'));

    await waitFor(() => expect(result.current.overlay.orders).toHaveLength(2));
    expect(result.current.overlay.orders).toEqual([
      { id: 'o1:stop', price: 5290, kind: 'stop', size: 2 },
      { id: 'o2:limit', price: 5320, kind: 'limit', size: 2 },
    ]);
    expect(result.current.overlay.position).toEqual({ averagePrice: 5300, netQuantity: 2 });
    expect(result.current.unavailable).toBe(false); // a confirmed Live read — the overlay is trustworthy
    // Owner-scoped by the active account AND instrument-scoped server-side (gh#772).
    expect(getWorkingOrdersMock).toHaveBeenCalledWith('acc-1', 'ES');
    expect(getPositionsMock).toHaveBeenCalledWith('acc-1', 'ES');
  });

  it('has an empty overlay and never loads when there is no ready account', async () => {
    useAccountsMock.mockReturnValue({ status: 'loading' });

    const { result } = renderHook(() => useExecutionOverlays('ES'));

    await waitFor(() => expect(result.current.overlay).toEqual({ orders: [], position: null }));
    expect(getWorkingOrdersMock).not.toHaveBeenCalled();
    expect(getPositionsMock).not.toHaveBeenCalled();
  });

  it('drops a flat position — a flat book is no line, never a line at 0', async () => {
    getPositionsMock.mockResolvedValue({
      ok: true,
      data: {
        markBasis: 'Live',
        positions: [{ contract: 'C', netQuantity: 0, averagePrice: 0, isFlat: true }],
      },
    });

    const { result } = renderHook(() => useExecutionOverlays('ES'));

    await waitFor(() => expect(getPositionsMock).toHaveBeenCalled());
    expect(result.current.overlay.position).toBeNull();
  });

  it('refreshes on an order-state push, a fill push, and a resync (gh#683 as signals, not marker data)', async () => {
    renderHook(() => useExecutionOverlays('ES'));
    await waitFor(() => expect(getWorkingOrdersMock).toHaveBeenCalledTimes(1));

    act(() => orderStateHandler?.());
    await waitFor(() => expect(getWorkingOrdersMock).toHaveBeenCalledTimes(2));

    act(() => fillHandler?.());
    await waitFor(() => expect(getWorkingOrdersMock).toHaveBeenCalledTimes(3));

    act(() => resyncHandler?.());
    await waitFor(() => expect(getWorkingOrdersMock).toHaveBeenCalledTimes(4));
  });

  it('flags the overlay stale whenever the socket is not live (R-19)', async () => {
    setRealtime('reconnecting');

    const { result } = renderHook(() => useExecutionOverlays('ES'));

    await waitFor(() => expect(result.current.stale).toBe(true));
  });

  it('leaves the overlay empty AND flags it unavailable when a read is refused / fails (not a flat book)', async () => {
    getWorkingOrdersMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'nope',
    });
    getPositionsMock.mockResolvedValue({ ok: false, kind: 'failed', status: 500, error: 'boom' });

    const { result } = renderHook(() => useExecutionOverlays('ES'));

    // A refused / failed read did not obtain venue truth, so the empty overlay is declared-unknown, not a flat book.
    await waitFor(() => expect(result.current.unavailable).toBe(true));
    expect(result.current.overlay).toEqual({ orders: [], position: null });
  });

  it('flags the overlay unavailable when the venue-truth basis is Unknown, never a confirmed flat (R-13)', async () => {
    // An unreachable venue is a 200 with markBasis 'Unknown' and empty data (gh#772). The overlay is empty, but that
    // is declared-unknown — the chart labels it rather than letting it read as flat. `stale` (the socket) does not
    // cover it: the socket is live here.
    getWorkingOrdersMock.mockResolvedValue({
      ok: true,
      data: { markBasis: 'Unknown', orders: [] },
    });
    getPositionsMock.mockResolvedValue({ ok: true, data: { markBasis: 'Unknown', positions: [] } });

    const { result } = renderHook(() => useExecutionOverlays('ES'));

    await waitFor(() => expect(result.current.unavailable).toBe(true));
    expect(result.current.stale).toBe(false); // socket is live — unavailability is the venue read, not the socket
    expect(result.current.overlay).toEqual({ orders: [], position: null });
  });

  it('coalesces a burst of order-state / fill pushes into a single re-read (bounds broker reads)', async () => {
    renderHook(() => useExecutionOverlays('ES'));
    await waitFor(() => expect(getWorkingOrdersMock).toHaveBeenCalledTimes(1)); // the immediate initial load

    // A partial-fill flurry in one tick — several pushes before any re-read completes.
    act(() => {
      orderStateHandler?.();
      fillHandler?.();
      fillHandler?.();
      orderStateHandler?.();
    });

    // The whole burst collapses to ONE additional pair of reads, not four.
    await waitFor(() => expect(getWorkingOrdersMock).toHaveBeenCalledTimes(2));
    expect(getPositionsMock).toHaveBeenCalledTimes(2);
  });

  it('drops a slow response for the instrument just left (R-14 token guard)', async () => {
    // ES's read resolves LATE, after a switch to NQ — the stale ES response must never overwrite NQ's overlay.
    let resolveEs: (value: unknown) => void = () => {};
    getWorkingOrdersMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveEs = resolve;
        }),
    );

    const { result, rerender } = renderHook(
      ({ instrument }: { instrument: string }) => useExecutionOverlays(instrument),
      { initialProps: { instrument: 'ES' } },
    );
    await waitFor(() => expect(getWorkingOrdersMock).toHaveBeenCalledWith('acc-1', 'ES'));

    // Switch to NQ; its read resolves immediately with one order.
    getWorkingOrdersMock.mockResolvedValue({
      ok: true,
      data: {
        markBasis: 'Live',
        orders: [
          {
            venueOrderKey: 'nq',
            contract: 'C',
            stopPrice: 100,
            limitPrice: null,
            size: 1,
            isProtective: true,
          },
        ],
      },
    });
    rerender({ instrument: 'NQ' });
    await waitFor(() => expect(result.current.overlay.orders).toHaveLength(1));
    expect(result.current.overlay.orders[0].id).toBe('nq:stop');

    // The stale ES read resolves now, with DIFFERENT data — the token guard must drop it.
    await act(async () => {
      resolveEs({
        ok: true,
        data: {
          markBasis: 'Live',
          orders: [
            {
              venueOrderKey: 'es1',
              contract: 'C',
              stopPrice: 1,
              limitPrice: null,
              size: 1,
              isProtective: true,
            },
            {
              venueOrderKey: 'es2',
              contract: 'C',
              stopPrice: 2,
              limitPrice: null,
              size: 1,
              isProtective: true,
            },
          ],
        },
      });
    });

    // Still NQ's single order — the late ES response was dropped, never rendered on NQ's chart.
    expect(result.current.overlay.orders).toHaveLength(1);
    expect(result.current.overlay.orders[0].id).toBe('nq:stop');
  });
});
