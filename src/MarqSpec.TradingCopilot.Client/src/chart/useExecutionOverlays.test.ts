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

  it('leaves the overlay empty when a read is refused / fails — the chart draws no stale marks', async () => {
    getWorkingOrdersMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'nope',
    });
    getPositionsMock.mockResolvedValue({ ok: false, kind: 'failed', status: 500, error: 'boom' });

    const { result } = renderHook(() => useExecutionOverlays('ES'));

    await waitFor(() => expect(getWorkingOrdersMock).toHaveBeenCalled());
    expect(result.current.overlay).toEqual({ orders: [], position: null });
  });
});
