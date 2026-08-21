import { act, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { FakeBroadcastChannel, installFakeBroadcastChannel } from '../testing/fakeBroadcastChannel';
import {
  LINKED_INSTRUMENT_CHANNEL,
  WINDOW_ORIGIN,
  decodeInstrumentMessage,
  encodeInstrumentMessage,
  isEchoInstrumentMessage,
  useLinkedInstrument,
} from './linkedInstrument';

afterEach(() => {
  FakeBroadcastChannel.reset();
});

describe('encode / decode', () => {
  it('round-trips an instrument message through encode → decode', () => {
    const decoded = decodeInstrumentMessage(encodeInstrumentMessage('NQ', 'origin-a'));

    expect(decoded).toEqual({ kind: 'instrument', instrument: 'NQ', origin: 'origin-a' });
  });

  it('tolerates malformed / garbage input by returning null, never throwing', () => {
    // A non-string, non-JSON, wrong-shape, wrong-kind, or empty-field payload is not an instrument message — the
    // channel is same-origin but a stray or future message on it must never crash or move the chart.
    expect(decodeInstrumentMessage('not json at all {')).toBeNull();
    expect(decodeInstrumentMessage('42')).toBeNull();
    expect(decodeInstrumentMessage('null')).toBeNull();
    expect(decodeInstrumentMessage('{"kind":"other","instrument":"ES","origin":"o"}')).toBeNull();
    expect(decodeInstrumentMessage('{"kind":"instrument","origin":"o"}')).toBeNull();
    expect(
      decodeInstrumentMessage('{"kind":"instrument","instrument":"","origin":"o"}'),
    ).toBeNull();
    expect(
      decodeInstrumentMessage('{"kind":"instrument","instrument":"ES","origin":42}'),
    ).toBeNull();
    expect(decodeInstrumentMessage(undefined)).toBeNull();
    expect(
      decodeInstrumentMessage({ kind: 'instrument', instrument: 'ES', origin: 'o' }),
    ).toBeNull();
  });
});

describe('echo detection', () => {
  it('flags a message from this window as an echo (ignored), and one from another window as not (applied)', () => {
    const mine = decodeInstrumentMessage(encodeInstrumentMessage('ES', WINDOW_ORIGIN));
    const theirs = decodeInstrumentMessage(encodeInstrumentMessage('ES', 'another-window'));

    expect(mine).not.toBeNull();
    expect(theirs).not.toBeNull();
    // Same origin as this window ⇒ our own broadcast bouncing back — ignore it, so no window authors the other's state.
    expect(isEchoInstrumentMessage(mine!, WINDOW_ORIGIN)).toBe(true);
    expect(isEchoInstrumentMessage(theirs!, WINDOW_ORIGIN)).toBe(false);
  });
});

describe('useLinkedInstrument', () => {
  it('mirrors the initial instrument locally', () => {
    const restore = installFakeBroadcastChannel();
    try {
      const { result } = renderHook(() => useLinkedInstrument('ES'));
      expect(result.current[0]).toBe('ES');
    } finally {
      restore();
    }
  });

  it('posts to the channel AND updates local state when set', () => {
    const restore = installFakeBroadcastChannel();
    try {
      // A peer window listening on the same channel captures what this window broadcasts.
      const received: unknown[] = [];
      const peer = new FakeBroadcastChannel(LINKED_INSTRUMENT_CHANNEL);
      peer.addEventListener('message', (event) => received.push(event.data));

      const { result } = renderHook(() => useLinkedInstrument('ES'));
      act(() => result.current[1]('NQ'));

      expect(result.current[0]).toBe('NQ'); // local mirror moved
      expect(received).toHaveLength(1);
      expect(decodeInstrumentMessage(received[0])).toEqual({
        kind: 'instrument',
        instrument: 'NQ',
        origin: WINDOW_ORIGIN, // tagged with THIS window so the peer can tell it apart from its own echoes
      });
    } finally {
      restore();
    }
  });

  it('moves local state when another window broadcasts an instrument (non-echo)', () => {
    const restore = installFakeBroadcastChannel();
    try {
      const peer = new FakeBroadcastChannel(LINKED_INSTRUMENT_CHANNEL);
      const { result } = renderHook(() => useLinkedInstrument('ES'));

      act(() => peer.postMessage(encodeInstrumentMessage('CL', 'another-window')));

      expect(result.current[0]).toBe('CL'); // followed the other window — neither is authoritative, last write wins
    } finally {
      restore();
    }
  });

  it('ignores an echo of its OWN broadcast — it does not re-apply a message tagged with this window', () => {
    const restore = installFakeBroadcastChannel();
    try {
      const peer = new FakeBroadcastChannel(LINKED_INSTRUMENT_CHANNEL);
      const { result } = renderHook(() => useLinkedInstrument('ES'));

      // A message carrying THIS window's origin (as our own post would, relayed back) must be dropped, not applied.
      act(() => peer.postMessage(encodeInstrumentMessage('ZZ', WINDOW_ORIGIN)));

      expect(result.current[0]).toBe('ES'); // unchanged — the echo was suppressed
    } finally {
      restore();
    }
  });

  it('ignores malformed traffic on the channel without throwing or moving', () => {
    const restore = installFakeBroadcastChannel();
    try {
      const peer = new FakeBroadcastChannel(LINKED_INSTRUMENT_CHANNEL);
      const { result } = renderHook(() => useLinkedInstrument('ES'));

      act(() => peer.postMessage('garbage-not-json'));

      expect(result.current[0]).toBe('ES');
    } finally {
      restore();
    }
  });

  it('unsubscribes and closes its channel on unmount', () => {
    const restore = installFakeBroadcastChannel();
    try {
      // A peer keeps the name registered, so the count reflects the hook's own channel opening and closing.
      const peer = new FakeBroadcastChannel(LINKED_INSTRUMENT_CHANNEL);
      expect(FakeBroadcastChannel.liveCount(LINKED_INSTRUMENT_CHANNEL)).toBe(1);

      const { unmount } = renderHook(() => useLinkedInstrument('ES'));
      expect(FakeBroadcastChannel.liveCount(LINKED_INSTRUMENT_CHANNEL)).toBe(2); // hook opened one

      unmount();
      expect(FakeBroadcastChannel.liveCount(LINKED_INSTRUMENT_CHANNEL)).toBe(1); // hook closed its own on unmount

      // And a post after unmount neither throws nor is observed by the torn-down hook.
      expect(() => peer.postMessage(encodeInstrumentMessage('CL', 'another-window'))).not.toThrow();
    } finally {
      restore();
    }
  });

  it('degrades to local-only when BroadcastChannel is unavailable — never throws', () => {
    // An older browser (or a test env) with no BroadcastChannel: the chart still tracks its own instrument, it just
    // does not sync across windows. Degrade, never crash (the app's posture everywhere).
    const restore = installFakeBroadcastChannel(undefined);
    try {
      const { result } = renderHook(() => useLinkedInstrument('ES'));

      expect(() => act(() => result.current[1]('NQ'))).not.toThrow();
      expect(result.current[0]).toBe('NQ'); // local mirror still works with no channel
    } finally {
      restore();
    }
  });
});
