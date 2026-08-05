/**
 * The service worker's cache boundary (gh#650, R-19). The installable PWA is a **presentation client only**: the
 * app *shell* is cached for a fast, offline-capable launch, but **server state is never cached**. A cached position
 * read is a lie about money, and an offline-looking kill-switch state is worse — the operator would read protection
 * that is not there. So every request under a state path must go to the network and show *unavailable* rather than
 * *stale* (the same declared-unknown posture `PositionReconciliationService` takes server-side, R-19 / ADR-0013).
 *
 * This module is the single source of truth for that boundary. There is no hand-written `sw.ts`: Workbox's
 * `generateSW` mode builds the worker from {@link PWA_WORKBOX_OPTIONS} below, which `vite.config.ts` uses
 * directly rather than a copy of it. `sw-cache-policy.test.ts` asserts both {@link NETWORK_ONLY_PATH} and
 * {@link PWA_WORKBOX_OPTIONS} directly — the acceptance criterion "no state read is cache-served" is proven by
 * importing the actual wiring, not by inspecting the built worker.
 */

/**
 * Matches every path that returns server state — account, order, position, suggestion, risk and market data all
 * live under `/api/` — plus the `/health` and `/ready` liveness probes. A request matching this is served from the
 * network or not at all; it is never cached, and the shell's navigation fallback never answers it.
 */
export const NETWORK_ONLY_PATH = /^\/(api|health|ready)(\/|$)/i;

/**
 * True when a path must always reach the network and may never be served (or written) from cache. Belt-and-braces
 * companion to {@link NETWORK_ONLY_PATH} for call sites that hold a pathname rather than a route pattern.
 */
export function isNetworkOnlyPath(pathname: string): boolean {
  return NETWORK_ONLY_PATH.test(pathname);
}

/**
 * The full Workbox `generateSW` options for the installable shell (gh#650, R-19 / ADR-0010). `vite.config.ts`
 * assigns `workbox:` to this object directly — there is nowhere else in the codebase that key is set — so, like
 * {@link NETWORK_ONLY_PATH}, this is a single source of truth a test can assert on directly instead of inspecting
 * the built worker. Deliberately carries **no** `runtimeCaching` key: adding one for `/api` would opt live state
 * into the cache, exactly the regression `sw-cache-policy.test.ts` exists to make impossible to add unnoticed.
 */
export const PWA_WORKBOX_OPTIONS = {
  // Precache the app SHELL only — the static bundle assets. No API response is a build artifact, so no server
  // state is ever precached.
  globPatterns: ['**/*.{js,css,html,svg,png,ico,webmanifest,woff,woff2}'],
  navigateFallback: '/index.html',
  // A client-side route is answered with the cached shell offline, but a state path (/api, /health, /ready) is
  // denied it and must reach the network — a stale shell must never stand in for live state.
  navigateFallbackDenylist: [NETWORK_ONLY_PATH],
};
