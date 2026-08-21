import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * The cross-window linked-instrument channel (gh#1015, ADR-0006). Selecting an instrument in one window moves the
 * chart in every other same-origin window — the docked workspace and a detached chart pop-out follow each other —
 * over the browser's {@link BroadcastChannel}, **without either window authoring the other's state**: every window
 * keeps its own local mirror, a change is a *broadcast* not a command, and last-write-wins. This is deliberately
 * **local UX coordination only** (ADR-0006): market / account / execution truth still rides SignalR from the server;
 * the channel carries a symbol, never a risk limit or an order.
 *
 * The message logic is kept **pure** ({@link encodeInstrumentMessage} / {@link decodeInstrumentMessage} /
 * {@link isEchoInstrumentMessage}) so it unit-tests with zero DOM — jsdom's `BroadcastChannel` is unreliable and the
 * real one is asynchronous and cross-window. The multi-window wiring itself is the Playwright E2E ADR-0006 notes.
 */

/** The same-origin channel name every window's linked-instrument mirror joins. */
export const LINKED_INSTRUMENT_CHANNEL = 'tc.linked-instrument';

/** The wire shape a window broadcasts when its operator picks an instrument. */
export interface InstrumentMessage {
  readonly kind: 'instrument';
  /** The selected instrument symbol (already normalized by the picker — the channel is a transparent mirror). */
  readonly instrument: string;
  /** The originating window's id, so a window can drop the echo of its OWN broadcast (see {@link isEchoInstrumentMessage}). */
  readonly origin: string;
}

/** A per-window origin id — one per module load, i.e. one per browser window / tab. Client code, so a UUID is fine. */
function createWindowOrigin(): string {
  const uuid = globalThis.crypto?.randomUUID?.();
  // Degrade, never crash: a context without crypto.randomUUID still gets a serviceable per-window id.
  return uuid ?? `w-${Math.random().toString(36).slice(2)}-${Date.now().toString(36)}`;
}

/** This window's origin id. Distinct per window in production; fixed per module load within a single test process. */
export const WINDOW_ORIGIN: string = createWindowOrigin();

/** Serialize an instrument selection into the channel's JSON envelope, tagged with the originating window. */
export function encodeInstrumentMessage(instrument: string, origin: string): string {
  return JSON.stringify({ kind: 'instrument', instrument, origin } satisfies InstrumentMessage);
}

/**
 * Parse a channel payload back into an {@link InstrumentMessage}, **tolerating anything** — a non-string, non-JSON,
 * wrong-shape, wrong-kind, or empty-field payload returns `null` rather than throwing. A same-origin channel can still
 * carry a stray or future message, and a malformed one must never crash the reader or move the chart.
 */
export function decodeInstrumentMessage(raw: unknown): InstrumentMessage | null {
  if (typeof raw !== 'string') {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof parsed !== 'object' || parsed === null) {
    return null;
  }
  const candidate = parsed as Record<string, unknown>;
  if (
    candidate.kind !== 'instrument' ||
    typeof candidate.instrument !== 'string' ||
    candidate.instrument.length === 0 ||
    typeof candidate.origin !== 'string' ||
    candidate.origin.length === 0
  ) {
    return null;
  }
  return { kind: 'instrument', instrument: candidate.instrument, origin: candidate.origin };
}

/** Whether a message is this window's own broadcast bounced back — its origin equals ours, so it must be ignored. */
export function isEchoInstrumentMessage(message: InstrumentMessage, origin: string): boolean {
  return message.origin === origin;
}

/** The sessionStorage key the current instrument is mirrored under, for a window opened from here to seed from. */
const LINKED_INSTRUMENT_SEED_KEY = 'tc.linked-instrument.seed';

/**
 * Persist the current instrument to `sessionStorage` (gh#1017). A `window.open()`ed pop-out inherits a **copy** of its
 * opener's sessionStorage at open time, so mirroring the instrument here lets a freshly detached chart open on the
 * instrument the docked window was showing — passive shared storage, not a handshake, so neither window authors the
 * other's state. Best-effort: a disabled / quota-exceeded sessionStorage is swallowed (the seed is a convenience).
 */
export function writeLinkedInstrumentSeed(instrument: string): void {
  try {
    globalThis.sessionStorage?.setItem(LINKED_INSTRUMENT_SEED_KEY, instrument);
  } catch {
    // sessionStorage can throw (private-mode quota, disabled); never crash for an open-time convenience.
  }
}

/**
 * The instrument a window opened from here should seed on (gh#1017) — the value {@link writeLinkedInstrumentSeed} last
 * mirrored to `sessionStorage`, or `null` when there is none (a fresh session, an empty or unreadable value) so the
 * caller falls back to its own default. Ongoing cross-window sync still rides the channel; this is only the open-time
 * seed, so a detached chart starts on the docked instrument instead of a fixed default.
 */
export function readLinkedInstrumentSeed(): string | null {
  try {
    const seed = globalThis.sessionStorage?.getItem(LINKED_INSTRUMENT_SEED_KEY);
    return seed !== null && seed !== undefined && seed.length > 0 ? seed : null;
  } catch {
    return null; // an unreadable sessionStorage must never crash the chart — degrade to the default
  }
}

/** Open the linked-instrument channel, or `null` if the environment has no `BroadcastChannel` (degrade to local-only). */
function openLinkedInstrumentChannel(): BroadcastChannel | null {
  const Channel = globalThis.BroadcastChannel;
  if (typeof Channel === 'undefined') {
    return null;
  }
  try {
    return new Channel(LINKED_INSTRUMENT_CHANNEL);
  } catch {
    // A locked-down environment can throw on construction; degrade to a local-only mirror rather than crash.
    return null;
  }
}

/**
 * A window's linked-instrument state: a **local mirror** of the selected instrument that syncs across same-origin
 * windows (gh#1015). `set` updates this window AND broadcasts to the others; a non-echo broadcast from another window
 * updates this window. Neither window is authoritative — each holds its own copy and the last write wins. Falls back
 * to a purely local mirror when `BroadcastChannel` is unavailable, and unsubscribes / closes on unmount.
 *
 * @param initial the instrument to start on (used once, on mount, like any `useState` seed)
 * @returns `[instrument, setInstrument]` — the current instrument and a setter that also broadcasts
 */
export function useLinkedInstrument(initial: string): [string, (next: string) => void] {
  const [instrument, setInstrument] = useState(initial);
  const channelRef = useRef<BroadcastChannel | null>(null);

  // Open the channel ONCE per mount and subscribe. No unstable dependency here (the inbound handler drives local via
  // setState, which never loops) — the gh#749 render-loop trap is avoided by an empty dep list, not a memoized array.
  useEffect(() => {
    const channel = openLinkedInstrumentChannel();
    channelRef.current = channel;
    if (channel === null) {
      return; // degrade to local-only: the setter still updates local state, it just does not broadcast
    }
    const handleMessage = (event: MessageEvent): void => {
      const message = decodeInstrumentMessage(event.data);
      // Drop garbage and our own echo; otherwise follow the other window (last write wins, neither authoritative).
      if (message === null || isEchoInstrumentMessage(message, WINDOW_ORIGIN)) {
        return;
      }
      setInstrument(message.instrument);
    };
    channel.addEventListener('message', handleMessage);
    return () => {
      channel.removeEventListener('message', handleMessage);
      channel.close();
      channelRef.current = null;
    };
  }, []);

  const setLinkedInstrument = useCallback((next: string) => {
    setInstrument(next);
    writeLinkedInstrumentSeed(next); // gh#1017: seed a pop-out opened from this window with the current instrument
    channelRef.current?.postMessage(encodeInstrumentMessage(next, WINDOW_ORIGIN));
  }, []);

  return [instrument, setLinkedInstrument];
}
