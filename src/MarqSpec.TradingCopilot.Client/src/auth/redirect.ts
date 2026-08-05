import type { Location } from 'react-router';

/**
 * Where to send an operator after they authenticate: back to the path they were trying to reach when the guard
 * turned them away ({@link RequireAuth} stashes it in `location.state.from`), or the workspace if they came to
 * sign-in directly.
 *
 * `location.state` is untyped, so it is read defensively — and only a **same-origin relative path** is honoured.
 * An absolute URL (or a protocol-relative `//host`) is discarded: a stale or hostile `from` must never turn the
 * post-login redirect into an open redirect off the operator's cockpit.
 */
export function redirectTargetOf(location: Location): string {
  const state: unknown = location.state;
  if (state !== null && typeof state === 'object' && 'from' in state) {
    const from = (state as { from: unknown }).from;
    if (typeof from === 'string' && from.startsWith('/') && !from.startsWith('//')) {
      return from;
    }
  }
  return '/';
}
