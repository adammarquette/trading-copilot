/**
 * Detaching a workspace panel into its own window (gh#651, ADR-0006).
 *
 * A detached panel is nothing more than the **same single-origin SPA** loaded fresh at a route. So it boots its
 * own authenticated session (the JWT is shared same-origin, ADR-0003) and — because {@link RealtimeProvider} is
 * mount-scoped, not a module singleton — its **own** SignalR connection, torn down when the window closes. It
 * carries the safety strip like any other window ({@link DetachedPanelFrame}). This is a **view** concern only:
 * no window is ever a second execution path (ADR-0006).
 */

/** The pop-out chrome: a real OS window (not a tab), sized for a single panel on a second monitor. */
const PANEL_WINDOW_FEATURES = 'popup=yes,width=520,height=760';

/** The route a detached panel renders at; {@link DetachedPanelPage} resolves the `:panelId`. */
export function panelRoute(panelId: string): string {
  return `/panel/${panelId}`;
}

/**
 * Detaches a panel into its own window. The window **name** is deterministic per panel, so re-detaching the same
 * panel **reuses** the window it already opened rather than stacking duplicates — the browser re-targets that named
 * window (navigating it, possibly a brief reload) and `focus()` raises it. Returns the handle, or `null` when the
 * browser blocked the pop-up — the caller stays on its feet rather than dereferencing it.
 */
export function detachPanel(panelId: string): Window | null {
  const handle = window.open(
    panelRoute(panelId),
    `copilot-panel-${panelId}`,
    PANEL_WINDOW_FEATURES,
  );
  handle?.focus();
  return handle;
}
