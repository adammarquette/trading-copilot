import { afterEach, describe, expect, it, vi } from 'vitest';

// A fresh fake hub is built per (re)connect — the manual-reconnect design rebuilds — so we record every build to
// inspect the URL (the resume cursor) and reach the registered handlers / close callbacks.
interface BuildRecord {
  url: string;
  tokenFactory?: () => string | null;
  hub: {
    start: ReturnType<typeof vi.fn>;
    stop: ReturnType<typeof vi.fn>;
    handlers: Map<string, (...args: unknown[]) => void>;
    closeCallbacks: Array<(error?: Error) => void>;
  };
}

const { builds } = vi.hoisted(() => ({ builds: [] as BuildRecord[] }));

vi.mock('@microsoft/signalr', () => {
  // A class, not an arrow `vi.fn` — SignalR is used with `new`, and vitest cannot construct an arrow mock.
  class HubConnectionBuilder {
    private readonly record: Partial<BuildRecord> = {};
    withUrl(url: string, options?: { accessTokenFactory?: () => string | null }) {
      this.record.url = url;
      this.record.tokenFactory = options?.accessTokenFactory;
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      const handlers = new Map<string, (...args: unknown[]) => void>();
      const closeCallbacks: Array<(error?: Error) => void> = [];
      this.record.hub = {
        start: vi.fn().mockResolvedValue(undefined),
        stop: vi.fn().mockResolvedValue(undefined),
        handlers,
        closeCallbacks,
        on: vi.fn((name: string, callback: (...args: unknown[]) => void) =>
          handlers.set(name, callback),
        ),
        onclose: vi.fn((callback: (error?: Error) => void) => closeCallbacks.push(callback)),
      } as unknown as BuildRecord['hub'];
      builds.push(this.record as BuildRecord);
      return this.record.hub;
    }
  }
  return { HubConnectionBuilder, LogLevel: { Warning: 3 } };
});

import { createRealtimeConnection } from './connection';
import { RealtimeCatchUpPhase, RealtimeMethod } from './messages';

afterEach(() => {
  builds.length = 0;
  vi.clearAllMocks();
});

function event(sequence: number, type = 'market.quote') {
  return { sequence, type, occurredAt: '2026-01-01T00:00:00Z', payload: '{}' };
}

describe('createRealtimeConnection', () => {
  it('connects to the hub with the JWT and no cursor on a first connect', async () => {
    const connection = createRealtimeConnection({}, { tokenFactory: () => 'jwt-123' });

    await connection.start();

    expect(builds).toHaveLength(1);
    expect(builds[0].url).toBe('/hubs/realtime'); // no ?after on a first connect
    expect(builds[0].tokenFactory?.()).toBe('jwt-123'); // fed to accessTokenFactory
    expect(builds[0].hub.start).toHaveBeenCalledOnce();
    expect(connection.state).toBe('live');
  });

  it('dedupes events at or below the applied sequence (idempotent resume)', async () => {
    const onEvent = vi.fn();
    const connection = createRealtimeConnection({ onEvent });
    await connection.start();
    const deliver = builds[0].hub.handlers.get(RealtimeMethod.Event)!;

    deliver(event(5));
    deliver(event(5)); // exact redelivery
    deliver(event(3)); // older than the high-water mark
    deliver(event(6)); // strictly newer

    expect(onEvent).toHaveBeenCalledTimes(2); // only 5 and 6 advance state
    expect(onEvent.mock.calls.map((call) => (call[0] as { sequence: number }).sequence)).toEqual([
      5, 6,
    ]);
  });

  it('resumes from the last applied sequence on reconnect, and signals resync', async () => {
    const onResynced = vi.fn();
    const connection = createRealtimeConnection({ onResynced }, { reconnectDelayMs: 0 });
    await connection.start();
    builds[0].hub.handlers.get(RealtimeMethod.Event)!(event(42));
    expect(onResynced).not.toHaveBeenCalled(); // not on the first connect

    // The socket drops.
    builds[0].hub.closeCallbacks.forEach((callback) => callback());
    expect(connection.state).toBe('reconnecting');

    await vi.waitFor(() => expect(connection.state).toBe('live'));
    expect(builds).toHaveLength(2);
    expect(builds[1].url).toBe('/hubs/realtime?after=42'); // rebuilt with the current cursor, not the stale one
    expect(onResynced).toHaveBeenCalledOnce(); // owner-scoped state must be re-fetched
  });

  it('forwards a retention gap so the surface re-fetches', async () => {
    const onGap = vi.fn();
    const connection = createRealtimeConnection({ onGap });
    await connection.start();

    const gap = {
      requestedAfterSequence: 10,
      oldestAvailableSequence: 20,
      oldestAvailableOccurredAt: '',
    };
    builds[0].hub.handlers.get(RealtimeMethod.Gap)!(gap);

    expect(onGap).toHaveBeenCalledWith(gap);
  });

  it('forwards an owner-scoped suggestion push to onSuggestion', async () => {
    const onSuggestion = vi.fn();
    const connection = createRealtimeConnection({ onSuggestion });
    await connection.start();

    const suggestion = { suggestionId: 's1', state: 'Active', at: '2026-01-01T00:00:00Z' };
    builds[0].hub.handlers.get(RealtimeMethod.Suggestion)!(suggestion);

    expect(onSuggestion).toHaveBeenCalledWith(suggestion);
  });

  it('forwards an owner-scoped chat message push to onChatMessage (gh#1063)', async () => {
    const onChatMessage = vi.fn();
    const connection = createRealtimeConnection({ onChatMessage });
    await connection.start();

    const message = {
      conversationId: 'c1',
      messageId: 'm1',
      sequence: 2,
      role: 2,
      content: 'headroom is $1,800',
      at: '2026-01-01T00:00:00Z',
    };
    builds[0].hub.handlers.get(RealtimeMethod.ChatMessage)!(message);

    expect(onChatMessage).toHaveBeenCalledWith(message);
  });

  it('forwards a streamed chat chunk to onChatChunk (gh#1063 inc 3b)', async () => {
    const onChatChunk = vi.fn();
    const connection = createRealtimeConnection({ onChatChunk });
    await connection.start();

    const chunk = { conversationId: 'c1', delta: 'Headroom is ' };
    builds[0].hub.handlers.get(RealtimeMethod.ChatChunk)!(chunk);

    expect(onChatChunk).toHaveBeenCalledWith(chunk);
  });

  it('tags events inside a catch-up bracket as historical, and live outside it', async () => {
    const onEvent = vi.fn();
    const connection = createRealtimeConnection({ onEvent });
    await connection.start();
    const hub = builds[0].hub;

    hub.handlers.get(RealtimeMethod.CatchUp)!({ phase: RealtimeCatchUpPhase.Started, sequence: 1 });
    hub.handlers.get(RealtimeMethod.Event)!(event(2, 'killswitch.engaged'));
    hub.handlers.get(RealtimeMethod.CatchUp)!({
      phase: RealtimeCatchUpPhase.Completed,
      sequence: 3,
    });
    hub.handlers.get(RealtimeMethod.Event)!(event(4, 'killswitch.engaged'));

    expect(onEvent).toHaveBeenNthCalledWith(1, expect.objectContaining({ sequence: 2 }), true); // history
    expect(onEvent).toHaveBeenNthCalledWith(2, expect.objectContaining({ sequence: 4 }), false); // live
  });

  it('does not leave a live event mislabelled historical after a drop mid catch-up bracket', async () => {
    // A catch-up bracket that never Completed (the socket dropped inside it) must not poison the next connection:
    // a fresh socket is not mid-catch-up until the server says so. Leaving it stuck would render a *live*
    // kill-switch / auto-flatten as history — the unsafe direction (R-19 / ADR-0013).
    const onEvent = vi.fn();
    const connection = createRealtimeConnection({ onEvent }, { reconnectDelayMs: 0 });
    await connection.start();
    builds[0].hub.handlers.get(RealtimeMethod.CatchUp)!({
      phase: RealtimeCatchUpPhase.Started,
      sequence: 1,
    });
    // Drop before a Completed ever arrives.
    builds[0].hub.closeCallbacks.forEach((callback) => callback());
    await vi.waitFor(() => expect(connection.state).toBe('live'));

    // The resume replays no bracket; the next event is live and must be tagged live.
    builds[1].hub.handlers.get(RealtimeMethod.Event)!(event(2, 'killswitch.engaged'));

    expect(onEvent).toHaveBeenCalledWith(expect.objectContaining({ sequence: 2 }), false);
  });

  it('reports state through the lifecycle and stops cleanly', async () => {
    const states: string[] = [];
    const connection = createRealtimeConnection({ onStateChange: (state) => states.push(state) });

    await connection.start();
    await connection.stop();

    expect(states).toEqual(['connecting', 'live', 'down']);
    expect(builds[0].hub.stop).toHaveBeenCalledOnce();
    expect(connection.state).toBe('down');
  });
});
