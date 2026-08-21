# ADR-0006: Multi-screen workspace — detachable pop-out panels (web-native)

**Status:** Accepted · **Date:** 2026-07-18 · **Deciders:** Adam (operator)
**Relates to:** PRD §6 *Nice-to-Have* (Multi-screen workspace), `R-10` (chart — central surface), `R-6` (chat),
`R-11` (execution UI), `R-13` (auto-flatten), `R-18` (auth); engineering §2;
[ADR-0003](0003-authentication.md) (JWT / SignalR), [ADR-0005](0005-ui-design-language.md) (design language).
Resolves the *"decide multi-monitor / detachable panels"* follow-up in ADR-0005. Wireframes: [`../design/`](../design/).

## Context
A desk trader runs **multiple monitors** and wants to **spread the workspace** — e.g. the chart on one screen, the
order-flow / DOM pane on another, and journal + news + positions on a third. The app is a **React SPA** (engineering
§2), authenticated by **JWT**, already receiving live state over **SignalR** on an **outbox + monotonic-sequence +
idempotent-resume** durability pattern (ADR-0003). Multi-window must keep **every window's live data consistent**,
must **not require an install**, and must **never introduce a second execution path**.

## Decision
- **Web-native multi-window — no desktop shell.** Panels **detach into pop-out windows** (`window.open`) the operator
  drags onto other monitors, keeping the **single-origin SPA** (ADR-0005). No Electron/Tauri install.
- **Workspace-per-window presets.** Each window renders a saved **layout preset** (e.g. *Chart*, *Order flow*,
  *Journal + news*), **addressable by route** so a window can be bookmarked and reopened on its monitor.
- **Server-authoritative live state — each window is just another SignalR client.** A pop-out opens its own
  authenticated SignalR connection and subscribes to the state it shows (quotes, positions/orders/fills, suggestions,
  alerts). The existing **outbox + monotonic-sequence + idempotent-resume** backbone already makes every client
  consistent, so a second window is **"free"** — no separate cross-window data channel for market / account /
  execution state.
- **BroadcastChannel for local UI coordination.** Same-origin, window-to-window **UI** state that shouldn't
  round-trip the server — active instrument, **linked crosshair**, layout, theme — syncs over the browser's
  **BroadcastChannel**, so panels can be *linked* (change symbol in one, the others follow) without server chatter.
- **Optional auto-placement via the Window Management API.** Where supported (Chromium `getScreenDetails()`,
  permission-gated), the app can **enumerate screens and place/restore pop-outs** (remember which panel lives on
  which monitor). **Enhancement, not a dependency** — the fallback is manual drag with persisted presets.
- **Execution stays single-authority.** Order placement, the risk gate, kill switch, and auto-flatten remain
  **server-side and identical regardless of which window initiated them** (R-5 / R-11 / R-13 / R-16). Multi-window
  is a **view** concern, never a second execution path. The **kill switch + time-to-flat countdown appear in every
  window** (ADR-0005).

## Alternatives considered
- **Desktop shell (Electron / Tauri).** True native multi-window and OS integration, but adds an install, a
  build/update pipeline, and a native-security surface, and abandons the single-origin web SPA. **Kept as a future
  option** if web pop-outs prove insufficient — the same React app can be wrapped later, so the decision is
  reversible.
- **Single-window, CSS multi-pane only** (today's adaptive layout stretched across a spanned window). Simple, but
  can't place panels on **separate** monitors independently — doesn't meet the ask.
- **Client-only cross-tab sync (SharedWorker / BroadcastChannel) as the data backbone.** Would duplicate what
  SignalR already does; rejected for market / account / execution state (the server is the source of truth). Used
  **only** for local UI coordination.

## Consequences
**Positive**
- Multi-monitor spread with **no install**, **reusing the SignalR backbone** — each window is a consistent client.
- **Layout presets** (+ optional auto-placement) give a repeatable desk setup.
- Execution authority stays **centralized**; safety controls replicate to every window.

**Negative / costs**
- **Cross-window UI coordination** (linked symbol / crosshair, layout persistence) is **new front-end work**
  (BroadcastChannel + a layout store).
- **Auth across pop-outs (R-18):** each window needs the JWT/session without re-prompting — share the token across
  same-origin windows (BroadcastChannel relay or shared storage) and handle per-window refresh.
- **Window Management API** support is uneven (Chromium-only, permissioned) — treat auto-placement as an enhancement.
- **Pop-out lifecycle** (reconnect / resume on reopen) leans on the existing idempotent-resume — exercise it per
  window.

## Update (2026-08-17) — the pop-out foundation lands (gh#651, increment 1)

The first slice of the decision above is built, in `src/MarqSpec.TradingCopilot.Client/src/panels`: a workspace
panel detaches into its own window, and that window carries the safety strip.

- **Route-addressable pop-outs.** A detached panel is the *same* single-origin SPA loaded fresh at
  `/panel/:panelId` (`DetachedPanelPage`), so it is bookmarkable and reopenable on its monitor. `detachPanel(id)`
  is a `window.open` to that route under a **deterministic per-panel window name**, so re-detaching *focuses* the
  window it already opened rather than stacking duplicates. The **suggestion feed** and the **live blotter** are
  the first two detachable panels; the chart (which needs the cross-window linked-instrument channel) and chat
  (not yet built) follow.
- **Each window is its own SignalR client — for free.** `RealtimeProvider` is **mount-scoped** (a component that
  owns one connection), not a module singleton, so a pop-out — a separate document with its own React root — opens
  its **own** connection and tears it down when the window closes. The "connection manager per-window" this ADR
  called for is a property of *where the provider sits*, not new machinery; the trap the issue flagged was
  converting it to a singleton, which this increment deliberately does not.
- **The safety strip is in every window — decided, and drawn in the wireframes.** The "Watch" this task carried
  (does a detached window carry the kill switch + time-to-flat countdown?) is settled the way the Decision above
  and ADR-0005 already point: **yes, always — present and functional in every window.** `DetachedPanelFrame`
  mounts the same `SafetyRegion` the shell does, and even a mistyped `/panel/…` URL renders inside that frame — a
  wrong address is never a window without a kill switch. Both controls **act** from any window, and execution stays
  single-authority: a pop-out is a **view**, never a second execution path, and the kill switch / auto-flatten stay
  **server-enforced** regardless of what any window displays. The *displayed* kill / countdown state is now
  **live-synced across windows too (gh#985)**: `KillSwitchControl` and `TimeToFlat` subscribe to the realtime
  safety-strip events (an exact set — the kill-switch engage / disengage / escalate events, and the deadline-passed
  auto-flatten outcomes) plus `onResync`, re-reading on each — the READ is the truth, the broadcast only the prompt —
  exactly as `ProtectionStatus` already did. So an engage / disengage in one window, or a fired auto-flatten,
  refreshes every other window's display promptly rather than lagging by the periodic refresh; a stale pop-out never
  claims trading is halted when it is not (or the reverse). A low-frequency backstop re-read on the kill switch
  bounds the staleness a missed or failed event read could otherwise leave on that bidirectionally-read control.
- **Auth across windows (R-18) is same-origin storage.** The JWT lives in `localStorage` (ADR-0003), shared across
  same-origin windows, so a pop-out boots authenticated through `RequireAuth` without re-prompting.
- **Layout persistence + reattach landed (gh#651 increment 2).** Which panels are detached is persisted to
  `localStorage` (`panelLayout` / `usePanelLayout`) and restored on load, so a reload **remembers which panels were
  detached and offers to reattach them** rather than silently re-docking. The panel control is now a **dock toggle**
  (pop out when docked, reattach when detached), and reattach closes the window this tab opened. This is the layout
  *preference* surviving — a step toward the acceptance's *"survives a restart in its last layout"*, not the full
  window arrangement: the pop-out **windows themselves do not reappear** (a browser cannot reopen a window without a
  user gesture), so auto-reopen rides the `BroadcastChannel` increment below. Two cross-window limits are deliberately left to the `BroadcastChannel` increment: a browser cannot
  re-open a window without a user gesture (so a reload shows the detached panels *as* detached, with a reattach
  affordance, rather than auto-reopening them), and reattaching a pop-out that outlived a main-window reload re-docks
  the layout but cannot itself close that now-orphaned window.

**Deferred to later increments** (each an enhancement on this foundation, none of it changing the decision): the
`BroadcastChannel` linked-instrument / crosshair coordination (and the cross-window reattach-close / auto-reopen it
enables), the **chart** panel, and the Window Management API auto-placement.

## Update (2026-08-20) — the chart panel + linked-instrument channel land (gh#1015, increment 3)

The chart becomes the third detachable panel, and the `BroadcastChannel` coordination the Decision called for is
built for its first payload — the **active instrument** — completing gh#651's scope. In
`src/MarqSpec.TradingCopilot.Client/src`:

- **A cross-window linked-instrument channel — neither window authoritative.** `panels/linkedInstrument.ts` opens a
  same-origin `BroadcastChannel` (`tc.linked-instrument`) carrying a typed `{ kind: 'instrument'; instrument; origin }`
  envelope. Each window holds its **own** local mirror of the charted instrument and *broadcasts* a change rather than
  commanding one: `useLinkedInstrument(initial)` updates local state **and** posts on `set`, and applies an inbound
  message only when it is **not this window's own echo** (a per-window `origin` id — `crypto.randomUUID()` — is stamped
  on every post and compared on receipt). So selecting ES in the docked workspace moves a detached chart, and selecting
  NQ in the detached chart moves the docked one, with **no window owning the other's state** — last write wins. This is
  the ADR's *"local UI coordination, not the data backbone"* line held exactly: the channel carries a **symbol**, never
  a risk limit or an order; market / account / execution truth still rides SignalR from the server. The message logic is
  **pure** (`encode` / `decode` tolerating malformed input by returning `null`, never throwing / `isEcho`) so it
  unit-tests with zero DOM, and the hook **degrades to a local-only mirror** when `BroadcastChannel` is absent (an older
  browser) rather than throwing — the app's degrade-never-crash posture.
- **The chart is now a detachable pop-out panel.** It registers in `DETACHABLE_PANELS` (`panels/panelRegistry.ts`) and
  detaches / reattaches / survives a reload exactly as the suggestion feed and blotter do (the gh#651 increment-2
  `usePanelLayout` machinery, unchanged). A shared `chart/ChartWorkspace` column (the chart + its instrument /
  resolution / indicator controls + the suggestion / execution / fill overlay hooks) is extracted from
  `WorkspaceSurface` and rendered **identically** docked and in the detached `panels/DetachedChart` pop-out — one
  column, no JSX duplication. Resolution / indicators / the level overlay stay **per-window** view state (only the
  instrument is linked); the docked `WorkspaceSurface` now drives its instrument through `useLinkedInstrument` instead
  of local `useState`, its behavior otherwise unchanged.
- **The detached chart carries the safety strip — for free.** As a registry panel it frames through
  `DetachedPanelFrame` like the others, so the kill switch + time-to-flat countdown are present and functional in the
  chart's own window too (the ADR-0005/0006 invariant), and execution stays single-authority: the pop-out chart is a
  **view**, never a second execution path.
- **Chat stays out.** The chat panel remains deferred (gh#18, not yet built); only the chart joined this increment.

The **unit-testable seam** is what landed here — the pure message functions, the hook's post / apply / echo-suppress /
unmount / degrade behavior, the registry entry, and the docked / detached composition all have vitest coverage. The
**multi-window** behavior itself (two real windows syncing over a real `BroadcastChannel`) is the **Playwright E2E**
already named in the Follow-ups below (*"`BroadcastChannel` local UI state sync"*), which a browser, not jsdom, must
exercise.

## Follow-ups
- Define **layout presets** + persistence. *(The detached-set persistence landed in gh#651 increment 2 —
  `localStorage`; server-saved presets per operator remain.)*
- Design **token/session sharing across same-origin windows** (R-18) and **per-window SignalR resume**. *(The
  same-origin-`localStorage` half landed with gh#651 increment 1; per-window resume rides the existing
  idempotent-resume, exercised per window.)*
- **Spike** the Window Management API placement with a manual-drag fallback.
- ~~Extend the wireframes with a **detached / multi-screen arrangement**.~~ *Done (gh#651): the "Detached —
  multi-screen desk" screen.*
- Author **Playwright E2E UI tests** covering pop-out window detachment, SignalR multi-window state resume, and `BroadcastChannel` local UI state sync once the SPA lands.
