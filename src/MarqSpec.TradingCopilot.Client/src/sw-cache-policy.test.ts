import { describe, expect, it } from 'vitest';

import { isNetworkOnlyPath, NETWORK_ONLY_PATH } from './sw-cache-policy';

// The acceptance criterion for gh#650: "No state read is cache-served — asserted by a test, not by inspection."
// A cached position read is a lie about money, and an offline-looking kill-switch state is worse. This suite is
// that assertion: every server-state family is network-only (never cached, never answered by the offline shell),
// while the app shell and its static assets are not.
describe('sw-cache-policy', () => {
  const stateReads = [
    '/api/accounts',
    '/api/orders',
    '/api/orders/00000000-0000-0000-0000-000000000000',
    '/api/positions',
    '/api/workingorders',
    '/api/suggestions',
    '/api/risk/daily-headroom',
    '/api/killswitch',
    '/api/marketdata/bars',
    '/api/marketdata/indicators',
    '/api/marketdata/levels',
    '/api/conditionals/abc/reconcile',
  ];

  it.each(stateReads)('treats the state read %s as network-only, never cache-served', (path) => {
    expect(isNetworkOnlyPath(path)).toBe(true);
  });

  it.each(['/health', '/ready'])('treats the liveness probe %s as network-only', (path) => {
    expect(isNetworkOnlyPath(path)).toBe(true);
  });

  const shell = [
    '/',
    '/index.html',
    '/assets/index-abc123.js',
    '/assets/index-def456.css',
    '/manifest.webmanifest',
    '/icons/icon-192.png',
    '/suggestions', // a client-side ROUTE (no /api prefix) — the shell may answer this offline
    '/blotter',
  ];

  it.each(shell)('leaves the shell asset / client route %s cacheable', (path) => {
    expect(isNetworkOnlyPath(path)).toBe(false);
  });

  it('does not mistake a client route that merely contains "api" for a state read', () => {
    // Guards the anchor: only a leading /api segment is state; /apiary or /capital is a normal client route.
    expect(isNetworkOnlyPath('/apiary')).toBe(false);
    expect(isNetworkOnlyPath('/capital')).toBe(false);
  });

  it('exposes the same boundary as a route pattern for the worker navigation denylist', () => {
    // sw.ts denylists NETWORK_ONLY_PATH so an /api navigation is never answered by the cached shell.
    expect(NETWORK_ONLY_PATH.test('/api/positions')).toBe(true);
    expect(NETWORK_ONLY_PATH.test('/suggestions')).toBe(false);
  });
});
