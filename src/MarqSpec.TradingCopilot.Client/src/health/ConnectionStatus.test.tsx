import { cleanup, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { RealtimeConnectionState } from '../realtime/connection';
import { renderWithProviders } from '../testing/render';
import { ConnectionStatus } from './ConnectionStatus';

const { realtimeMock } = vi.hoisted(() => ({ realtimeMock: vi.fn() }));
vi.mock('../realtime/RealtimeProvider', () => ({ useOptionalRealtime: () => realtimeMock() }));

afterEach(() => {
  cleanup();
  realtimeMock.mockReset();
});

function withState(connectionState: RealtimeConnectionState): void {
  realtimeMock.mockReturnValue({ connectionState });
}

function status() {
  return screen.getByRole('status');
}

describe('ConnectionStatus', () => {
  it('reads the realtime connection state, not a /health poll', () => {
    withState('live');

    renderWithProviders(<ConnectionStatus />);

    expect(status().dataset.status).toBe('live');
    expect(status().textContent).toContain('Live');
  });

  it('labels a reconnecting socket so a stale view is not mistaken for live', () => {
    withState('reconnecting');

    renderWithProviders(<ConnectionStatus />);

    expect(status().dataset.status).toBe('reconnecting');
    expect(status().textContent).toContain('Reconnecting');
  });

  it('shows offline when the socket is down', () => {
    withState('down');

    renderWithProviders(<ConnectionStatus />);

    expect(status().dataset.status).toBe('down');
    expect(status().textContent).toContain('Offline');
  });

  it('is not optimistically green while connecting', () => {
    withState('connecting');

    renderWithProviders(<ConnectionStatus />);

    // A socket that has not connected is not a connection that works.
    expect(status().dataset.status).toBe('connecting');
  });

  it('falls back to down outside a provider, never a false green', () => {
    realtimeMock.mockReturnValue(null);

    renderWithProviders(<ConnectionStatus />);

    expect(status().dataset.status).toBe('down');
  });
});
