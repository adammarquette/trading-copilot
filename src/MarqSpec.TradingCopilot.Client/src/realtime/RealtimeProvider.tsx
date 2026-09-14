import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { ReactNode } from 'react';

import { useAuth } from '../auth/AuthProvider';
import { createRealtimeConnection } from './connection';
import type { RealtimeConnection, RealtimeConnectionState } from './connection';
import type {
  RealtimeChatChunk,
  RealtimeChatMessage,
  RealtimeChatTurnFaulted,
  RealtimeEvent,
  RealtimeFill,
  RealtimeOrderState,
  RealtimeSuggestion,
} from './messages';

type EventHandler = (event: RealtimeEvent, historical: boolean) => void;
type OrderStateHandler = (state: RealtimeOrderState) => void;
type FillHandler = (fill: RealtimeFill) => void;
type SuggestionHandler = (suggestion: RealtimeSuggestion) => void;
type ChatMessageHandler = (message: RealtimeChatMessage) => void;
type ChatChunkHandler = (chunk: RealtimeChatChunk) => void;
type ChatTurnFaultedHandler = (faulted: RealtimeChatTurnFaulted) => void;
type ResyncHandler = () => void;

export interface RealtimeContextValue {
  /** The socket's visible state — surfaces read this to label a degraded view rather than render stale-as-live. */
  readonly connectionState: RealtimeConnectionState;
  /** Subscribe to broadcast events (market data + safety strip). `historical` is true inside a catch-up bracket. */
  onEvent(handler: EventHandler): () => void;
  onOrderState(handler: OrderStateHandler): () => void;
  onFill(handler: FillHandler): () => void;
  /** Subscribe to owner-scoped suggestion lifecycle pushes (gh#684) — a new / superseded suggestion. */
  onSuggestion(handler: SuggestionHandler): () => void;
  /** Subscribe to owner-scoped chat message pushes (gh#906, gh#1063) — a newly appended turn on any connection. */
  onChatMessage(handler: ChatMessageHandler): () => void;
  /** Subscribe to streamed chat token deltas (gh#906 inc 3b, gh#1063) — presentation-only, best-effort. */
  onChatChunk(handler: ChatChunkHandler): () => void;
  /**
   * Subscribe to a faulted turn's terminator (gh#1107) — the turn streamed and will never settle, so the draft it
   * opened has to be retired. Keyed by conversation, which is sufficient: the server refuses a second in-flight
   * turn on one (gh#1106).
   */
  onChatTurnFaulted(handler: ChatTurnFaultedHandler): () => void;
  /**
   * Fired when the client must re-fetch its state over REST — a retention gap, or a reconnect (owner-scoped
   * order/fill/suggestion pushes are live-only and are not replayed). A surface subscribes to reload its REST reads.
   */
  onResync(handler: ResyncHandler): () => void;
}

const RealtimeContext = createContext<RealtimeContextValue | null>(null);

/**
 * Owns the single realtime connection (gh#649) and exposes a subscribe API for surfaces. The socket's lifecycle
 * mirrors the auth session (gh#648): it starts once the operator is authenticated and stops the moment they are
 * not — so it tears down in lockstep with the token being cleared, without touching the API client's 401 handler
 * (which AuthProvider owns). Realtime data is global / operator-wide, so this lives above the shell, not per-account.
 */
export function RealtimeProvider({ children }: { children: ReactNode }): React.JSX.Element {
  const { session } = useAuth();
  const [connectionState, setConnectionState] = useState<RealtimeConnectionState>('down');

  // Stable subscriber registries — the connection's callbacks fan out to these, so a re-render never re-subscribes.
  const eventSubs = useRef(new Set<EventHandler>());
  const orderStateSubs = useRef(new Set<OrderStateHandler>());
  const fillSubs = useRef(new Set<FillHandler>());
  const suggestionSubs = useRef(new Set<SuggestionHandler>());
  const chatMessageSubs = useRef(new Set<ChatMessageHandler>());
  const chatChunkSubs = useRef(new Set<ChatChunkHandler>());
  const chatTurnFaultedSubs = useRef(new Set<ChatTurnFaultedHandler>());
  const resyncSubs = useRef(new Set<ResyncHandler>());

  const authenticated = session.status === 'authenticated';

  useEffect(() => {
    if (!authenticated) {
      return;
    }

    const connection: RealtimeConnection = createRealtimeConnection({
      onEvent: (event, historical) =>
        eventSubs.current.forEach((handler) => handler(event, historical)),
      onOrderState: (state) => orderStateSubs.current.forEach((handler) => handler(state)),
      onFill: (fill) => fillSubs.current.forEach((handler) => handler(fill)),
      onSuggestion: (suggestion) =>
        suggestionSubs.current.forEach((handler) => handler(suggestion)),
      onChatMessage: (message) => chatMessageSubs.current.forEach((handler) => handler(message)),
      onChatChunk: (chunk) => chatChunkSubs.current.forEach((handler) => handler(chunk)),
      onChatTurnFaulted: (faulted) =>
        chatTurnFaultedSubs.current.forEach((handler) => handler(faulted)),
      onGap: () => resyncSubs.current.forEach((handler) => handler()),
      onResynced: () => resyncSubs.current.forEach((handler) => handler()),
      onStateChange: setConnectionState,
    });

    void connection.start();
    return () => {
      void connection.stop();
    };
  }, [authenticated]);

  const onEvent = useCallback((handler: EventHandler) => subscribe(eventSubs.current, handler), []);
  const onOrderState = useCallback(
    (handler: OrderStateHandler) => subscribe(orderStateSubs.current, handler),
    [],
  );
  const onFill = useCallback((handler: FillHandler) => subscribe(fillSubs.current, handler), []);
  const onSuggestion = useCallback(
    (handler: SuggestionHandler) => subscribe(suggestionSubs.current, handler),
    [],
  );
  const onChatMessage = useCallback(
    (handler: ChatMessageHandler) => subscribe(chatMessageSubs.current, handler),
    [],
  );
  const onChatChunk = useCallback(
    (handler: ChatChunkHandler) => subscribe(chatChunkSubs.current, handler),
    [],
  );
  const onChatTurnFaulted = useCallback(
    (handler: ChatTurnFaultedHandler) => subscribe(chatTurnFaultedSubs.current, handler),
    [],
  );
  const onResync = useCallback(
    (handler: ResyncHandler) => subscribe(resyncSubs.current, handler),
    [],
  );

  const value = useMemo<RealtimeContextValue>(
    () => ({
      connectionState,
      onEvent,
      onOrderState,
      onFill,
      onSuggestion,
      onChatMessage,
      onChatChunk,
      onChatTurnFaulted,
      onResync,
    }),
    [
      connectionState,
      onEvent,
      onOrderState,
      onFill,
      onSuggestion,
      onChatMessage,
      onChatChunk,
      onChatTurnFaulted,
      onResync,
    ],
  );

  return <RealtimeContext.Provider value={value}>{children}</RealtimeContext.Provider>;
}

function subscribe<T>(registry: Set<T>, handler: T): () => void {
  registry.add(handler);
  return () => {
    registry.delete(handler);
  };
}

/** The realtime context; throws outside a {@link RealtimeProvider}. */
export function useRealtime(): RealtimeContextValue {
  const value = useContext(RealtimeContext);
  if (value === null) {
    throw new Error('useRealtime must be used within a RealtimeProvider.');
  }
  return value;
}

/** The realtime context, or `null` outside a provider — for chrome that may render before the provider is mounted. */
export function useOptionalRealtime(): RealtimeContextValue | null {
  return useContext(RealtimeContext);
}
