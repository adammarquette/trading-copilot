import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { useAccountsMock, useRealtimeMock, listMock } = vi.hoisted(() => ({
  useAccountsMock: vi.fn(),
  useRealtimeMock: vi.fn(),
  listMock: vi.fn(),
}));
vi.mock('../accounts/AccountProvider', () => ({ useAccounts: useAccountsMock }));
vi.mock('../realtime/RealtimeProvider', () => ({ useRealtime: useRealtimeMock }));
vi.mock('../api/suggestions', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/suggestions')>();
  return { ...actual, listActionableSuggestions: listMock };
});

import { SuggestionState } from '../api/suggestions';
import { useSuggestionZones } from './useSuggestionZones';

let resyncHandler: (() => void) | null = null;

function setRealtime(connectionState: string): void {
  useRealtimeMock.mockReturnValue({
    connectionState,
    onEvent: vi.fn(),
    onOrderState: vi.fn(),
    onFill: vi.fn(),
    onResync: (handler: () => void) => {
      resyncHandler = handler;
      return vi.fn();
    },
  });
}

beforeEach(() => {
  vi.clearAllMocks();
  resyncHandler = null;
  useAccountsMock.mockReturnValue({ status: 'ready', activeAccount: { id: 'acc-1' } });
  setRealtime('live');
  listMock.mockResolvedValue({ ok: true, data: [] });
});

afterEach(() => {
  resyncHandler = null;
});

describe('useSuggestionZones', () => {
  it('loads the active account, keeps only the charted instrument, and maps entry/stop/target', async () => {
    listMock.mockResolvedValue({
      ok: true,
      data: [
        {
          id: 's1',
          instrument: 'ES',
          state: SuggestionState.Active,
          entryPrice: 5300,
          stopPrice: 5290,
          targetPrice: 5320,
        },
        {
          id: 's2',
          instrument: 'NQ',
          state: SuggestionState.Active,
          entryPrice: 1,
          stopPrice: 2,
          targetPrice: 3,
        },
      ],
    });

    const { result } = renderHook(() => useSuggestionZones('ES'));

    await waitFor(() => expect(result.current.zones).toHaveLength(1));
    expect(result.current.zones[0]).toEqual({ id: 's1', entry: 5300, stop: 5290, target: 5320 });
    expect(listMock).toHaveBeenCalledWith('acc-1'); // owner-scoped by the active account
  });

  it('has no zones and never loads when there is no ready account', async () => {
    useAccountsMock.mockReturnValue({ status: 'loading' });

    const { result } = renderHook(() => useSuggestionZones('ES'));

    await waitFor(() => expect(result.current.zones).toEqual([]));
    expect(listMock).not.toHaveBeenCalled();
  });

  it('reloads on a realtime resync (reconnect / retention gap)', async () => {
    renderHook(() => useSuggestionZones('ES'));
    await waitFor(() => expect(listMock).toHaveBeenCalledTimes(1));

    act(() => resyncHandler?.());

    await waitFor(() => expect(listMock).toHaveBeenCalledTimes(2));
  });

  it('flags the zones stale whenever the socket is not live (R-19)', async () => {
    setRealtime('down');

    const { result } = renderHook(() => useSuggestionZones('ES'));

    await waitFor(() => expect(result.current.stale).toBe(true));
  });
});
