# ADR-0010: Progressive Web App (installable client)

**Status:** Accepted · **Date:** 2026-07-19 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-19` (installable client), `R-6`/`R-10`/`R-11` (the SPA surface), `R-13` (auto-flatten),
`R-18` (auth / HTTPS); engineering §1 (BFF + thin client), §2 (frontend); [ADR-0005](0005-ui-design-language.md)
(adaptive Material UI), [ADR-0006](0006-multi-screen-workspace.md) (multi-screen).

## Context
The React SPA (engineering §2) is the operator's workspace. Adam wants it **installable like an app** — a
home-screen icon that launches **standalone** — on **Android** and, best-effort, **iOS**. A trading UI gets used
away from the desk, so an installed app (icon, standalone window, and later web-push for flatten warnings) fits how
it's actually used. Hard constraint: the UI must **never** become a place where a guardrail is missed because the
client was closed or evicted.

## Decision
- **Ship the SPA as a PWA** — a **web-app manifest** (icons, theme, `display: standalone`, `start_url`) + a
  registered **service worker**, over **HTTPS** (R-18). On **Android / Chromium** this yields a real OS-level
  install (install prompt / Add to Home Screen) and standalone launch.
- **iOS / Safari is best-effort** — **Add to Home Screen** gives a standalone icon; we maintain the apple-touch icon
  + status-bar meta and **degrade gracefully** where iOS lacks parity (no programmatic install prompt, limited
  background, storage eviction, web-push only on iOS 16.4+ once installed). The app **detects and communicates**
  reduced capability rather than assuming it.
- **Presentation-only, safety server-side.** The PWA is a client of the BFF; the **risk gate (R-5), execution
  (R-11), auto-flatten (R-13), and kill switch are enforced server-side**, **independent of client state**. Closing,
  backgrounding, or OS-evicting the PWA cannot bust a flatten — the server owns it.
- **Service-worker scope is the app shell, not live data.** Cache the shell for fast launch and a usable
  **disconnected state** (an explicit "reconnecting / data stale" surface). **Never serve stale market data or risk
  state as live**, and **no order path while disconnected** (consistent with R-12 re-validation).
- **Composes with multi-screen (ADR-0006).** Desktop pop-out panels remain the desktop story; the installed PWA is
  the single-window mobile / tablet (and optional installed-desktop) experience.

## Alternatives considered
- **Native iOS / Android apps.** Best platform integration + reliable background / push, but a separate toolchain,
  app-store review, and duplicated UI — heavy for a single-operator tool. Rejected for v1; the PWA covers the need.
- **Plain responsive web (no install).** Simplest, but no home-screen presence, no standalone launch, weaker push.
  Rejected — the operator specifically wants an installable app.
- **Capacitor / native wrapper (web in a shell).** A middle path to the app stores + native APIs; kept as a **later
  option** if iOS PWA limits bite, since the SPA is reused as-is.

## Consequences
**Positive** — app-like home-screen / standalone UX on Android now; a clear path to away-from-desk **push** for
flatten warnings (P1); one codebase (the SPA) reused; safety unaffected (server-side).
**Negative / costs** — **iOS is a lesser experience** (Safari PWA limits) and must be tested honestly; a service
worker adds **cache-invalidation / update-flow** complexity (stale-shell risk) needing a disciplined update
strategy; offline scope must be **deliberately narrow** so nothing stale is ever shown as live.

## Follow-ups
- Author the **manifest + icon set** and the **service-worker update flow** (skip-waiting / prompt-to-refresh).
- Define the **disconnected-shell** UX (the "data stale / reconnecting" surface) and the no-order-while-offline guard.
- Validate **install + standalone** on Android and **Add to Home Screen** on iOS; record the capability matrix.
- Wire **web-push** for flatten warnings (P1) — **unblocked**: the notification channel is designed
  ([ADR-0019](0019-alerting-channel-and-thresholds.md), 2026-07-25). Web push lands as a **second adapter behind
  the same `INotificationChannel` seam**, not a competing path; Pushover carries the pager tier meanwhile, since
  a browser notification neither repeats until acknowledged nor bypasses Do Not Disturb.
- Author **Playwright E2E UI tests** covering SPA/PWA standalone launch, SignalR outbox sequence continuity on network drop, and multi-screen `BroadcastChannel` state parity once the SPA lands.
- **Landed** (gh#650): the **manifest + PNG icon set** (192 / 512 / maskable / apple-touch / favicon, generated from the design SVGs via [`generate-pwa-icons.mjs`](../design/logo/generate-pwa-icons.mjs)) and a **shell-only** service worker (Workbox `generateSW`, `prompt` update flow, **no `runtimeCaching`** — so no account / order / position / suggestion / risk read is ever cache-served, asserted by test), served same-origin by the BFF ([ADR-0020](0020-spa-served-by-the-bff.md)). **iOS caveat confirmed in practice:** Safari has no web-app-manifest support, so iOS is Add-to-Home-Screen only (apple-touch icon + status-bar metas), not an OS-level install. **Still open:** the Playwright standalone-launch E2E (above); a richer "data stale / reconnecting" surface beyond the shell's `/health` probe; the no-order-while-offline guard on the order path (gh#655); and web-push (P1).
