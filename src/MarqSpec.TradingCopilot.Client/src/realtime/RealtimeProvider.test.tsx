import { act, cleanup, render } from '@testing-library/react';
import { useEffect } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { Session } from '../auth/AuthProvider';
import type { RealtimeConnection, RealtimeConnectionCallbacks } from './connection';
import { RealtimeProvider, useRealtime } from './RealtimeProvider';

const { authMock, createConnectionMock } = vi.hoisted(() => ({
  authMock: vi.fn(),
  createConnectionMock: vi.fn(),
}));

vi.mock('../auth/AuthProvider', () => ({ useAuth: () => ({ session: authMock() }) }));
vi.mock('./connection', () => ({
  createRealtimeConnection: (callbacks: RealtimeConnectionCallbacks) =>
    createConnectionMock(callbacks),
}));

afterEach(() => {
  cleanup();
  authMock.mockReset();
  createConnectionMock.mockReset();
});

function authenticate(status: Session['status'] = 'authenticated'): void {
  authMock.mockReturnValue(
    status === 'authenticated' ? { status, user: { id: 'op' } } : { status },
  );
}

/** A fake transport that captures the callbacks the provider wires, so a test can drive them like the socket would. */
function fakeConnection(): {
  connection: RealtimeConnection;
  callbacks: () => RealtimeConnectionCallbacks;
} {
  let captured: RealtimeConnectionCallbacks = {};
  const connection = {
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    state: 'connecting',
  } as unknown as RealtimeConnection;
  createConnectionMock.mockImplementation((callbacks: RealtimeConnectionCallbacks) => {
    captured = callbacks;
    return connection;
  });
  return { connection, callbacks: () => captured };
}

describe('RealtimeProvider', () => {
  it('starts the connection once the operator is authenticated', () => {
    authenticate();
    const { connection } = fakeConnection();

    render(
      <RealtimeProvider>
        <div />
      </RealtimeProvider>,
    );

    expect(createConnectionMock).toHaveBeenCalledOnce();
    expect(connection.start).toHaveBeenCalledOnce();
  });

  it('does not connect while the session is anonymous or still loading', () => {
    authenticate('anonymous');

    render(
      <RealtimeProvider>
        <div />
      </RealtimeProvider>,
    );

    expect(createConnectionMock).not.toHaveBeenCalled(); // no socket without a token to authorize it
  });

  it('stops the connection on unmount (teardown in lockstep with the session)', () => {
    authenticate();
    const { connection } = fakeConnection();

    const { unmount } = render(
      <RealtimeProvider>
        <div />
      </RealtimeProvider>,
    );
    unmount();

    expect(connection.stop).toHaveBeenCalledOnce();
  });

  it('exposes the transport state, reflecting a degraded socket', () => {
    authenticate();
    const { callbacks } = fakeConnection();
    const seen: string[] = [];
    function Probe() {
      seen.push(useRealtime().connectionState);
      return null;
    }

    render(
      <RealtimeProvider>
        <Probe />
      </RealtimeProvider>,
    );
    expect(seen.at(-1)).toBe('down'); // honest default before the socket reports

    act(() => callbacks().onStateChange?.('reconnecting'));
    expect(seen.at(-1)).toBe('reconnecting');
  });

  it('fans events out to a subscriber and unsubscribes it on unmount — asserted, not assumed', () => {
    authenticate();
    const { callbacks } = fakeConnection();
    const received: number[] = [];
    function Consumer() {
      const { onEvent } = useRealtime();
      useEffect(() => onEvent((event) => received.push(event.sequence)), [onEvent]);
      return null;
    }

    const { unmount } = render(
      <RealtimeProvider>
        <Consumer />
      </RealtimeProvider>,
    );

    act(() =>
      callbacks().onEvent?.(
        { sequence: 1, type: 'market.quote', occurredAt: '', payload: '{}' },
        false,
      ),
    );
    expect(received).toEqual([1]);

    unmount();
    // The Consumer's effect cleanup must have removed it from the registry: a further push reaches no one.
    act(() =>
      callbacks().onEvent?.(
        { sequence: 2, type: 'market.quote', occurredAt: '', payload: '{}' },
        false,
      ),
    );
    expect(received).toEqual([1]); // still only the first — the unmounted subscriber did not fire
  });

  it('fans owner-scoped suggestion pushes out to a subscriber, and unsubscribes on unmount', () => {
    authenticate();
    const { callbacks } = fakeConnection();
    const received: string[] = [];
    function Consumer() {
      const { onSuggestion } = useRealtime();
      useEffect(
        () => onSuggestion((suggestion) => received.push(suggestion.suggestionId)),
        [onSuggestion],
      );
      return null;
    }

    const { unmount } = render(
      <RealtimeProvider>
        <Consumer />
      </RealtimeProvider>,
    );

    act(() => callbacks().onSuggestion?.({ suggestionId: 's1', state: 'Active', at: '' }));
    expect(received).toEqual(['s1']);

    unmount();
    act(() => callbacks().onSuggestion?.({ suggestionId: 's2', state: 'Superseded', at: '' }));
    expect(received).toEqual(['s1']); // the unmounted subscriber did not fire
  });

  it('fans owner-scoped chat message pushes out to a subscriber, and unsubscribes on unmount (gh#1063)', () => {
    authenticate();
    const { callbacks } = fakeConnection();
    const received: string[] = [];
    function Consumer() {
      const { onChatMessage } = useRealtime();
      useEffect(() => onChatMessage((message) => received.push(message.messageId)), [
        onChatMessage,
      ]);
      return null;
    }

    const { unmount } = render(
      <RealtimeProvider>
        <Consumer />
      </RealtimeProvider>,
    );

    act(() =>
      callbacks().onChatMessage?.({
        conversationId: 'c1',
        messageId: 'm1',
        sequence: 1,
        role: 2,
        content: 'hi',
        at: '',
      }),
    );
    expect(received).toEqual(['m1']);

    unmount();
    act(() =>
      callbacks().onChatMessage?.({
        conversationId: 'c1',
        messageId: 'm2',
        sequence: 2,
        role: 2,
        content: 'hi again',
        at: '',
      }),
    );
    expect(received).toEqual(['m1']); // the unmounted subscriber did not fire
  });

  it('fans streamed chat chunks out to a subscriber, and unsubscribes on unmount (gh#1063)', () => {
    authenticate();
    const { callbacks } = fakeConnection();
    const received: string[] = [];
    function Consumer() {
      const { onChatChunk } = useRealtime();
      useEffect(() => onChatChunk((chunk) => received.push(chunk.delta)), [onChatChunk]);
      return null;
    }

    const { unmount } = render(
      <RealtimeProvider>
        <Consumer />
      </RealtimeProvider>,
    );

    act(() => callbacks().onChatChunk?.({ conversationId: 'c1', delta: 'Head' }));
    expect(received).toEqual(['Head']);

    unmount();
    act(() => callbacks().onChatChunk?.({ conversationId: 'c1', delta: 'room' }));
    expect(received).toEqual(['Head']); // the unmounted subscriber did not fire
  });

  it('routes a resync (gap or reconnect) to resync subscribers', () => {
    authenticate();
    const { callbacks } = fakeConnection();
    const resyncs: string[] = [];
    function Consumer() {
      const { onResync } = useRealtime();
      useEffect(() => onResync(() => resyncs.push('resync')), [onResync]);
      return null;
    }

    render(
      <RealtimeProvider>
        <Consumer />
      </RealtimeProvider>,
    );

    act(() =>
      callbacks().onGap?.({
        requestedAfterSequence: 1,
        oldestAvailableSequence: 2,
        oldestAvailableOccurredAt: '',
      }),
    );
    act(() => callbacks().onResynced?.());

    expect(resyncs).toEqual(['resync', 'resync']); // both a retention gap and a reconnect drive a re-fetch
  });

  it('throws when useRealtime is used outside a provider', () => {
    function Orphan() {
      useRealtime();
      return null;
    }
    // Silence React's error-boundary console noise for the expected throw.
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    expect(() => render(<Orphan />)).toThrow(/RealtimeProvider/);
    spy.mockRestore();
  });
});
