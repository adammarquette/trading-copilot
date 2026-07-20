# ADR-0005: UI design language — dark-first Material, adaptive layout

**Status:** Accepted · **Date:** 2026-07-18 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-10` (charting — the central UI surface), `R-6` (chat), `R-11` (execution UI), `R-13`
(auto-flatten), `R-14` (mode safety), `R-18` (auth); engineering §2; [ADR-0004](0004-charting.md) (charting);
[architecture](../trading-platform-architecture.md) (UI). Wireframes: [`../design/`](../design/).

## Context
The operator's surface is a **React SPA** (engineering §2) whose **chart is the central component** (R-10,
ADR-0004), surrounded by the AI **suggestion + approve/reject** flow (R-4/R-11), the **order ticket**, live
**positions/fills**, the **journal**, the **rulebook**, and the **co-pilot chat + news feed** (R-6, R-2). It is a
**single-operator, information-dense, safety-critical cockpit**: the **time-to-flat countdown** (R-13) and the
**kill switch** must read at a glance, and status (long/short, risk headroom, connection) must be legible
instantly. A design language is needed before components are built.

Operator preferences (settled): a **dark theme**, **Material Design**, and an **adaptive layout**.

## Decision
- **Material Design (Material 3 / "Material You")** is the component + interaction language for the SPA.
- **Dark theme first.** The palette is expressed as **tokens** — surfaces + tonal elevation, one accent, on-colors;
  a **light theme is a token swap**, not a redesign — now **demonstrated in the wireframe** (dark default + a `[data-theme="light"]` toggle, gh#28). Neutrals are a **cool near-black biased toward the
  accent** (a chosen neutral, not flat grey).
- **Adaptive layout** on Material's **window size classes — compact / medium / expanded** — with **adaptive
  navigation**: **bottom navigation bar** (compact/phone) → **icon navigation rail** (medium/tablet) →
  **labelled navigation rail** (expanded/desktop). Secondary surfaces reflow: the right dock (suggestion / order /
  positions) and the chat + news dock collapse **panels → tabs → single stacked column**.
- **Semantic trading colors are separate from the accent.** long/profit (green), short/loss (red), time-to-flat
  (amber), and critical/kill (strong red) form their own scale, so **status never competes with the brand accent**
  (a blue kept deliberately off the trading semantics).
- **Type: Roboto / Roboto Mono**, with **tabular numerals** wherever prices / P&L align in columns.
- **Two safety controls are persistent at every breakpoint** — the **time-to-flat countdown** (R-13) and the
  **kill switch** — and the **practice/live mode chip** (R-14) is always in the app bar.
- **Implementation (likely — to confirm): MUI (Material UI) for React** as the component library; the central chart
  (**Lightweight Charts**, ADR-0004) is **themed to the same tokens** so it doesn't look foreign.

## Alternatives considered
- **Bespoke design system.** Maximum fit, but real cost to build and keep accessible for one operator; Material
  gives mature, accessible components and adaptive guidance off the shelf.
- **Other component systems** (Ant Design, Chakra, Fluent, Tailwind + headless). Viable, but the operator specified
  Material, and **MUI** is the mature Material implementation for React with strong dark-theme + token support.
- **Light theme first.** Rejected — operator preference is dark, and an all-day cockpit favors a dark ground; light
  is kept as a later token variant.
- **Fixed desktop-only layout.** Rejected — adaptive was an explicit requirement; the same app should degrade to a
  **monitor-and-approve** surface on a phone.

## Consequences
**Positive**
- A familiar, accessible, **token-driven** language; theming (light mode included — gh#28) is a token change.
- Adaptive navigation + reflow are a **solved Material pattern**; the cockpit works desktop → phone.
- Status reads at a glance because **semantic color is reserved for state**, not decoration.

**Negative / costs**
- The **charting library (ADR-0004) must be themed to the tokens** by hand — Lightweight Charts is not a Material
  component.
- Material's defaults can look **generic** — the token palette (cool neutrals, reserved accent, trading semantics)
  is what keeps it specific; hold that line.
- Dense trading data can fight Material's default spacing — expect to **tighten component density**.

## Follow-ups
- **Pick the React component library + version** (MUI likely) under Central Package Management; stand up the
  **token file** (surfaces, accent, semantic colors, type scale) as the single source of theming.
- **Theme the Lightweight Charts panes** to the tokens (grid / candle / overlay colors).
- **Decide multi-monitor / detachable panels** (a desk trader may want pop-out panels) and whether phone is
  monitor-and-approve only.
- **Light theme token variant — demonstrated** in the wireframe (`documentation/design/wireframes.html`: a `[data-theme="light"]` palette + targeted overrides for hardcoded on-container colors + a top-right ☀/☾ toggle; charts read the tokens, gh#28). Carry the same token variant into the **production SPA** (gh#23 / U1).
- Turn the wireframes into a **component inventory** — define the suggestion card, order ticket, and app-bar HUD
  as the first components.
