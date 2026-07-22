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

## Follow-ups
- Define **layout presets** + persistence (server-saved per operator vs. `localStorage`).
- Design **token/session sharing across same-origin windows** (R-18) and **per-window SignalR resume**.
- **Spike** the Window Management API placement with a manual-drag fallback.
- Extend the wireframes with a **detached / multi-screen arrangement**.
- Author **Playwright E2E UI tests** covering pop-out window detachment, SignalR multi-window state resume, and `BroadcastChannel` local UI state sync once the SPA lands.
