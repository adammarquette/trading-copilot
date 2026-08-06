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
import type { RealtimeEvent, RealtimeFill, RealtimeOrderState } from './messages';

type EventHandler = (event: RealtimeEvent, historical: boolean) => void;
type OrderStateHandler = (state: RealtimeOrderState) => void;
type FillHandler = (fill: RealtimeFill) => void;
type ResyncHandler = () => void;

export interface RealtimeContextValue {
  /** The socket's visible state — surfaces read this to label a degraded view rather than render stale-as-live. */
  readonly connectionState: RealtimeConnectionState;
  /** Subscribe to broadcast events (market data + safety strip). `historical` is true inside a catch-up bracket. */
  onEvent(handler: EventHandler): () => void;
  onOrderState(handler: OrderStateHandler): () => void;
  onFill(handler: FillHandler): () => void;
  /**
   * Fired when the client must re-fetch its state over REST — a retention gap, or a reconnect (owner-scoped
   * order/fill pushes are live-only and are not replayed). A surface subscribes to reload its REST reads.
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
  const onResync = useCallback(
    (handler: ResyncHandler) => subscribe(resyncSubs.current, handler),
    [],
  );

  const value = useMemo<RealtimeContextValue>(
    () => ({ connectionState, onEvent, onOrderState, onFill, onResync }),
    [connectionState, onEvent, onOrderState, onFill, onResync],
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
