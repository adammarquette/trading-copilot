/**
 * A small in-memory {@link BroadcastChannel} for tests (gh#1015). jsdom does not ship a reliable
 * `BroadcastChannel`, and Node's real one is process-global — so the linked-instrument channel
 * ({@link ../panels/linkedInstrument}) is tested against this deterministic fake instead.
 *
 * It mirrors the browser contract the hook relies on: a `postMessage` reaches every OTHER open
 * channel of the same name (never the posting instance itself — same-instance echo is a browser
 * guarantee), and `close()` unsubscribes. Delivery is **synchronous** so a test can assert right
 * after posting (wrap a post that drives a React `setState` in `act`).
 *
 * Install it over the global for a test with {@link installFakeBroadcastChannel}, which returns a
 * restore function; call {@link FakeBroadcastChannel.reset} between tests to drop any leaked peers.
 */
interface MessageLike {
  readonly data: unknown;
}
type MessageListener = (event: MessageLike) => void;

export class FakeBroadcastChannel {
  private static readonly registry = new Map<string, Set<FakeBroadcastChannel>>();

  /** Drop every open channel (call in `afterEach`, so a peer left open by one test never bleeds into the next). */
  static reset(): void {
    FakeBroadcastChannel.registry.clear();
  }

  /** How many channels of this name are currently open — lets a test prove the hook unsubscribed on unmount. */
  static liveCount(name: string): number {
    return FakeBroadcastChannel.registry.get(name)?.size ?? 0;
  }

  readonly name: string;
  onmessage: MessageListener | null = null;
  private readonly listeners = new Set<MessageListener>();
  private closed = false;

  constructor(name: string) {
    this.name = name;
    const peers = FakeBroadcastChannel.registry.get(name) ?? new Set<FakeBroadcastChannel>();
    peers.add(this);
    FakeBroadcastChannel.registry.set(name, peers);
  }

  postMessage(data: unknown): void {
    if (this.closed) {
      return;
    }
    const peers = FakeBroadcastChannel.registry.get(this.name);
    if (peers === undefined) {
      return;
    }
    for (const peer of peers) {
      // The browser never delivers a message to the instance that posted it; only to the other windows' channels.
      if (peer !== this && !peer.closed) {
        peer.deliver(data);
      }
    }
  }

  addEventListener(type: 'message', listener: MessageListener): void {
    if (type === 'message') {
      this.listeners.add(listener);
    }
  }

  removeEventListener(type: 'message', listener: MessageListener): void {
    if (type === 'message') {
      this.listeners.delete(listener);
    }
  }

  close(): void {
    this.closed = true;
    this.listeners.clear();
    this.onmessage = null;
    FakeBroadcastChannel.registry.get(this.name)?.delete(this);
  }

  private deliver(data: unknown): void {
    const event: MessageLike = { data };
    this.onmessage?.(event);
    for (const listener of this.listeners) {
      listener(event);
    }
  }
}

/**
 * Swap {@link FakeBroadcastChannel} in for the global `BroadcastChannel` and return a restore function. Pair with
 * `FakeBroadcastChannel.reset()` in `afterEach`. Passing `undefined` for the constructor simulates a browser
 * (or environment) with no `BroadcastChannel` at all, so the degrade-to-local-only path can be tested.
 */
export function installFakeBroadcastChannel(
  ctor:
    | typeof BroadcastChannel
    | undefined = FakeBroadcastChannel as unknown as typeof BroadcastChannel,
): () => void {
  const original = globalThis.BroadcastChannel as typeof BroadcastChannel | undefined;
  (globalThis as { BroadcastChannel?: typeof BroadcastChannel }).BroadcastChannel = ctor;
  return () => {
    (globalThis as { BroadcastChannel?: typeof BroadcastChannel }).BroadcastChannel = original;
  };
}
