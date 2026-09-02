import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import type { HubConnection } from '@microsoft/signalr';

import { readToken } from '../api/token';
import type {
  RealtimeCatchUp,
  RealtimeChatChunk,
  RealtimeChatMessage,
  RealtimeEvent,
  RealtimeFill,
  RealtimeGap,
  RealtimeOrderState,
  RealtimeSuggestion,
} from './messages';
import { RealtimeCatchUpPhase, RealtimeMethod } from './messages';

/**
 * The visible state of the socket (gh#649, R-19 / ADR-0013 / ADR-0019). A degraded socket must look degraded — the
 * repo's posture is declared-unknown over stale-but-confident — so the UI reads this rather than assuming live.
 */
export type RealtimeConnectionState = 'connecting' | 'live' | 'reconnecting' | 'down';

export interface RealtimeConnectionCallbacks {
  /** A broadcast event. `historical` is true while inside a server catch-up bracket — render as history, not live. */
  onEvent?: (event: RealtimeEvent, historical: boolean) => void;
  onOrderState?: (state: RealtimeOrderState) => void;
  onFill?: (fill: RealtimeFill) => void;
  /** A suggestion's new lifecycle state (gh#684). Owner-scoped + live-only like order / fill — a reconnect fires onResynced. */
  onSuggestion?: (suggestion: RealtimeSuggestion) => void;
  /** A newly appended chat message (gh#906, gh#1063). Owner-scoped + live-only, same as order / fill / suggestion. */
  onChatMessage?: (message: RealtimeChatMessage) => void;
  /** One streamed token delta of an in-flight chat turn (gh#906 inc 3b, gh#1063). Presentation-only, best-effort. */
  onChatChunk?: (chunk: RealtimeChatChunk) => void;
  /** The cursor fell off retention (or the resume was too large): re-fetch state over REST, then keep consuming. */
  onGap?: (gap: RealtimeGap) => void;
  /** Fired after a RE-connect: owner-scoped order/fill pushes are live-only (never replayed), so surfaces re-fetch. */
  onResynced?: () => void;
  onStateChange?: (state: RealtimeConnectionState) => void;
}

export interface RealtimeConnectionOptions {
  /** Milliseconds between reconnect attempts. Injected so tests need no real timers by default. */
  readonly reconnectDelayMs?: number;
  /** The JWT source; defaults to the shared token store (gh#648). */
  readonly tokenFactory?: () => string | null;
}

export interface RealtimeConnection {
  start(): Promise<void>;
  stop(): Promise<void>;
  readonly state: RealtimeConnectionState;
}

const HUB_PATH = '/hubs/realtime';
const DEFAULT_RECONNECT_MS = 2000;

/**
 * A single realtime connection to the BFF hub (gh#649). It owns reconnect and resume so surfaces do not have to.
 *
 * <b>Resume is manual, not `withAutomaticReconnect`.</b> SignalR's auto-reconnect reuses the URL captured at build
 * time, so it would re-send the original `?after=` (or none) and miss the outage backlog. Instead, on every
 * (re)connect this rebuilds the connection with `?after=<lastSequence>`, so the hub replays exactly what was missed;
 * the client then <b>dedupes by the monotonic `sequence`</b>, making the deliberate replay-vs-live overlap a no-op
 * (ADR-0021). Owner-scoped order/fill pushes carry no sequence and are live-only — a reconnect fires `onResynced`
 * so surfaces re-fetch that state over REST rather than assume they missed nothing.
 */
export function createRealtimeConnection(
  callbacks: RealtimeConnectionCallbacks,
  options: RealtimeConnectionOptions = {},
): RealtimeConnection {
  const reconnectDelayMs = options.reconnectDelayMs ?? DEFAULT_RECONNECT_MS;
  const tokenFactory = options.tokenFactory ?? readToken;

  let connection: HubConnection | null = null;
  let state: RealtimeConnectionState = 'down';
  let stopped = true;
  let lastSequence = 0;
  let connectedBefore = false;
  let inCatchUp = false;
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null;

  function setState(next: RealtimeConnectionState): void {
    if (state !== next) {
      state = next;
      callbacks.onStateChange?.(next);
    }
  }

  function register(hub: HubConnection): void {
    hub.on(RealtimeMethod.Event, (event: RealtimeEvent) => {
      // Dedupe: the resume replay deliberately overlaps live, so anything at or below the high-water mark was
      // already applied. Only a strictly-newer sequence advances state (and the cursor).
      if (event.sequence <= lastSequence) {
        return;
      }
      lastSequence = event.sequence;
      callbacks.onEvent?.(event, inCatchUp);
    });
    hub.on(RealtimeMethod.CatchUp, (catchUp: RealtimeCatchUp) => {
      inCatchUp = catchUp.phase === RealtimeCatchUpPhase.Started;
    });
    hub.on(RealtimeMethod.Gap, (gap: RealtimeGap) => callbacks.onGap?.(gap));
    hub.on(RealtimeMethod.OrderState, (orderState: RealtimeOrderState) =>
      callbacks.onOrderState?.(orderState),
    );
    hub.on(RealtimeMethod.Fill, (fill: RealtimeFill) => callbacks.onFill?.(fill));
    hub.on(RealtimeMethod.Suggestion, (suggestion: RealtimeSuggestion) =>
      callbacks.onSuggestion?.(suggestion),
    );
    hub.on(RealtimeMethod.ChatMessage, (message: RealtimeChatMessage) =>
      callbacks.onChatMessage?.(message),
    );
    hub.on(RealtimeMethod.ChatChunk, (chunk: RealtimeChatChunk) =>
      callbacks.onChatChunk?.(chunk),
    );
    hub.onclose(() => onClosed());
  }

  function buildUrl(): string {
    return lastSequence > 0 ? `${HUB_PATH}?after=${lastSequence}` : HUB_PATH;
  }

  async function connect(): Promise<void> {
    if (stopped) {
      return;
    }

    // A fresh socket is not mid-catch-up until the server brackets one on it. Resetting here stops a bracket that
    // never Completed (dropped inside it) from poisoning the reconnect and mislabelling live events as history.
    inCatchUp = false;

    try {
      // `.build()` is inside the try on purpose: it can throw (e.g. a URL it cannot resolve), and that is a
      // failed connect like any other — schedule a reconnect rather than escape as an unhandled rejection.
      const hub = new HubConnectionBuilder()
        .withUrl(buildUrl(), { accessTokenFactory: () => tokenFactory() ?? '' })
        .configureLogging(LogLevel.Warning)
        .build();
      register(hub);
      connection = hub;

      await hub.start();
      if (stopped) {
        // Stopped mid-handshake: don't leave a live socket behind.
        await hub.stop();
        return;
      }
      setState('live');
      if (connectedBefore) {
        callbacks.onResynced?.();
      }
      connectedBefore = true;
    } catch {
      scheduleReconnect();
    }
  }

  function onClosed(): void {
    if (stopped) {
      return;
    }
    scheduleReconnect();
  }

  function scheduleReconnect(): void {
    if (stopped || reconnectTimer !== null) {
      return;
    }
    setState('reconnecting');
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      void connect();
    }, reconnectDelayMs);
  }

  return {
    get state() {
      return state;
    },
    async start() {
      if (!stopped) {
        return;
      }
      stopped = false;
      setState('connecting');
      await connect();
    },
    async stop() {
      stopped = true;
      if (reconnectTimer !== null) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
      }
      setState('down');
      const hub = connection;
      connection = null;
      if (hub !== null) {
        await hub.stop();
      }
    },
  };
}
