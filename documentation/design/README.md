# Design

Design-time UI artifacts for the trading co-pilot's **React SPA**. The **UI design language** is recorded in
[ADR-0005](../adr/0005-ui-design-language.md) and summarized in
[engineering §2](../trading-platform-engineering.md); the surfaces trace to PRD
[R-10](../trading-platform-prd.md) (charting — the central surface), R-6 (chat), R-11 (execution), R-2 (news).

## Contents
- [`logo-concepts.html`](logo-concepts.html) — **brand mark / app-icon concepts** (four directions), self-contained;
  each shown at a size ladder (96→16 px), on a light surface, against the **PWA maskable safe zone**, and in
  context (browser tab, installed home screen, app bar beside the R-13 countdown + kill switch). Source SVGs in
  [`logo/`](logo/). Drawn in the [ADR-0005](../adr/0005-ui-design-language.md) tokens; the mark is the **accent
  blue**, never the trading semantics — see *Direction* below. Feeds the
  [ADR-0010](../adr/0010-progressive-web-app.md) follow-up "author the manifest + icon set" (R-19). **Provisionally
  leaning: E (Candle Reticle — the candle sequence framed by the HUD reticle, "a cockpit locked on a setup"; B + D)** — promoted to [`logo/app-icon.svg`](logo/app-icon.svg) (+
  [`app-icon-maskable.svg`](logo/app-icon-maskable.svg)) and wired into the wireframe app-bar; **open to change**.
  The full PNG icon set (192 / 512 / maskable / apple-touch / favicon) + `manifest.webmanifest` wiring remain the
  ADR-0010 follow-up.
- [`wireframes.html`](wireframes.html) — mid-fidelity wireframes in the dark-Material, adaptive language,
  self-contained (open in any browser, no build). Each region is tagged with the requirement it satisfies. Covers:
  - the **main trading workspace** across **desktop / tablet / phone**;
  - a **detached multi-screen desk** — pop-out windows per monitor ([ADR-0006](../adr/0006-multi-screen-workspace.md));
  - **strategy-performance breakdown** — taken vs. suggested (R-9), and **suggestion pass-feedback** (R-4);
  - the **order ticket** — side / size / type, bracket + safety stop, live risk readout, send now / on-trigger (R-11/R-5);
  - the **arm → edit → send** flow — Approve split-button, opt-in "send as-is", live risk-change warning (R-11/R-12);
  - **send-now vs. send-when-conditions-met** — native vs. synthetic/local orders that stay off the book (R-11);
  - **staged stop protection** — a hidden stop that goes native near the level, behind an always-native safety stop (R-11/R-5);
  - the **daily risk governor** — a personal cap inside the hard prop limit that throttles suggestions (R-5/R-4);
  - the **consistency target** — a personal daily profit target; on reach the governor **stands down** (suppress suggestions, optional stop-for-day), enforcing the prop consistency rule (R-5/R-9);
  - the **suggestion lifecycle** — active → stale → expired; scratched setups resolve as no-fill (R-4/R-9);
  - the **journal — P&L by day** — calendar + equity curve of realized daily P&L (R-8/R-9); and
  - **day detail & feedback** — drill into a day's trades, add feedback anytime, co-pilot asks follow-ups → rules (R-8/R-6); and
  - **account switcher** — one operator's many trading accounts grouped by platform login; the dropdown shows **only active + visible** accounts, with the complete roster + per-account hide toggle in Settings (R-17/R-14);
  - **settings — configuration** — a two-pane config surface gathering the cross-feature settings (risk tolerance / target R:R front and center, **per-market auto-flatten times** (null = the market's default), entry defaults, news, watchlist, layout, account); and
  - **news relevance & feedback** — the mapping / topics config panel (ticker↔instrument, per-instrument + global topics, AI-suggested) and per-item sentiment thumbs (R-2);
  - **backtest** — config (range / instruments / strategies / model) → summary metrics + refinement suggestions (ADR-0009); and
  - **AI usage & spend** — **operator-only**, reported in **Grafana** (not a trading surface) (§7 / ADR-0008 / ADR-0002); and
  - **sign-in** — login for a web-exposed deployment; the operator's account is seeded at first start, and data access is scoped to it at the data layer (R-18 / R-20); and
  - **connection & account onboarding** — add a firm login (platform → firm → server-side creds), discover its accounts, first sync (R-17 / R-18); and
  - **strategy templates ("playbooks")** — a gallery of packaged methodologies (13/48, ICT, VWAP-reclaim…) + a template's five-layer decomposition (indicators → setups → suggestion shape → risk → rules); install &amp; personalize (R-21); and
  - **rulebook & triggers** — plain-language rules compiled to confirmed deterministic triggers (mechanical-alert vs. agent-review), with debounce / rate-limit (R-7 / ADR-0008); and
  - **AI refinement loop** — journal feedback + backtests → the co-pilot's follow-up questions → confirmed rule / strategy refinements (R-6 / ADR-0009); and
  - **kill switch & auto-flatten** — the time-to-flat escalation, native-first flatten sequence, immutable audit trail, and the **connection-loss → synthetic-orphan emergency** state (R-13 / ADR-0007).

## Direction (see [ADR-0005](../adr/0005-ui-design-language.md))
- **Material Design (Material 3)**, **dark theme first** — a light theme is a **token** swap, not a redesign; now **demonstrated** via a top-right toggle in the wireframe (gh#28).
- **Adaptive layout** on Material's window size classes (compact / medium / expanded); **adaptive navigation**
  bottom bar → icon rail → labelled navigation rail; docks reflow panels → tabs → single column.
- **Chart-central** (R-10, [ADR-0004](../adr/0004-charting.md)) — the AI suggestion + approve/reject flow, order
  ticket, positions/fills, journal, rulebook, and co-pilot chat + news orbit it.
- **Semantic trading colors** (long/short, warn, critical) kept **distinct from the accent**; Roboto / Roboto Mono.
  This extends to the **brand mark**: the logo is drawn in the accent blue, *not* the conventional green
  up-and-to-the-right, so it never competes with live position status in the app bar (see
  [`logo-concepts.html`](logo-concepts.html)).
- **Safety controls always visible** at every breakpoint: the **time-to-flat countdown** (R-13) and the
  **kill switch**; the practice/live mode chip (R-14) sits in the app bar.
- **Multi-screen** ([ADR-0006](../adr/0006-multi-screen-workspace.md)) — detachable **pop-out panels** across
  monitors (web-native); each window is just another synced SignalR client. Illustrated as the **detached
  multi-screen desk** in the wireframes (order-flow · primary workspace · journal/news), with the kill switch +
  countdown replicated to every window.
- **Installable (PWA)** ([ADR-0010](../adr/0010-progressive-web-app.md)) — the SPA installs to the home screen and
  runs **standalone** (Android primary, iOS best-effort via Add-to-Home-Screen); the **phone / tablet** layouts are
  the installed-app experience, and safety stays server-side (R-19).

Wireframes are **mid-fidelity** — structure and language, not final visuals. Order-flow / DOM (Bookmap-style,
[ADR-0004](../adr/0004-charting.md)) is a later bespoke pane and isn't drawn here.
