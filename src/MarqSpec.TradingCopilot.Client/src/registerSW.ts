import { registerSW } from 'virtual:pwa-register';

/** Hooks the caller (the shell UI) wires to the service-worker lifecycle. Both are optional — the worker registers
 *  and caches the shell regardless; these only surface *visible* affordances for update and offline-readiness. */
export interface AppShellServiceWorkerCallbacks {
  /**
   * A new shell has been fetched and is waiting. Show a "refresh to update" affordance and call the supplied
   * `reload` when the operator accepts — the swap is never automatic, so an update cannot reload the page
   * mid-action (arming an order, watching the flatten countdown). ADR-0010's prompt-to-refresh.
   */
  onNeedRefresh?: (reload: () => void) => void;
  /** The shell is cached and the app can now launch offline. */
  onOfflineReady?: () => void;
}

/**
 * Registers the app-shell service worker (gh#650, R-19). Presentation-only: it caches the shell for an
 * offline-capable launch and, by {@link NETWORK_ONLY_PATH}, never caches server state.
 */
export function registerAppShellServiceWorker(
  callbacks: AppShellServiceWorkerCallbacks = {},
): void {
  // Service workers need a secure context and browser support; where absent (an insecure embed, an old browser)
  // the presentation client simply runs without an offline shell rather than throwing.
  if (!('serviceWorker' in navigator)) {
    return;
  }

  const updateServiceWorker = registerSW({
    immediate: true,
    onNeedRefresh() {
      callbacks.onNeedRefresh?.(() => {
        void updateServiceWorker(true);
      });
    },
    onOfflineReady() {
      callbacks.onOfflineReady?.();
    },
  });
}
