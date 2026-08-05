# ADR-0004: Charting — Lightweight Charts for the central chart; bespoke canvas/WebGL for order flow

**Status:** Accepted · **Date:** 2026-07-18 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-10` (charting — the central UI surface), `R-3` (order-flow analytics), `R-22` (indicator pipeline — the pre-computed indicator panes / overlays the chart renders), `R-1` (market data / DOM), engineering §2, [architecture](../trading-platform-architecture.md).

## Context
The **chart is the central UI component** (R-10) — a candlestick chart onto which indicators (incl. custom), price levels, suggestion zones, and live positions / orders / fills overlay. Requirements:
- Candlesticks (multi-resolution) + **indicator subcharts** (RSI, MACD, …) in their own panes.
- Custom indicators overlaid — which we **pre-compute** in the processor (ADR-0001), so the front only renders series.
- **No hand-drawing** needed (trendlines / freehand are out of scope).
- **Depth-of-Market + order flow** ("Bookmap-style" — a real-time liquidity heatmap over time × price, plus trades) as an **option**.
- Real-time, tick-fed → performance matters.

## Decision
- **Central candlestick chart + indicator subcharts: TradingView Lightweight Charts.** Canvas-based, ~45 KB, Apache-2.0, handles 50k+ candles in real time; **panes** carry the RSI / MACD subcharts; indicators, price lines, and markers render from **our pre-computed data** as overlays. (KLineCharts is an acceptable fallback — batteries-included indicators — but with drawing out of scope and indicators pre-computed, Lightweight Charts' performance + fit make it the choice.)
- **No built-in drawing tools** — dropped from scope.
- **DOM / order flow ("Bookmap-style") is a separate, bespoke visualization component** — *not* provided by any candlestick library. It renders a high-density, real-time **time × price liquidity heatmap + trades** from ProjectX's **`GatewayDepth`** (DOM) and **`GatewayTrade`** streams (see the wiki's ProjectX page). It needs **canvas or WebGL** rendering (SVG / d3 can't sustain the density) — its own component, feeding R-3.
- **d3 / canvas** for other bespoke views (footprint, volume profile), never the main chart.

## Alternatives considered
- **d3 for the main chart.** Rejected — reinvents pan/zoom/crosshair/perf a purpose-built library gives free; SVG struggles at tick scale.
- **KLineCharts.** Fine (built-in indicators + drawing), but drawing is out of scope and we pre-compute indicators, so its edge doesn't apply; kept as a documented fallback.
- **TradingView embed widget.** Can't carry *our* overlays (shows TradingView's data) — stays supplemental (R-10), not primary.
- **A charting library for the DOM heatmap.** None does Bookmap-class order flow off the shelf; it is inherently a bespoke component.

## Consequences
**Positive**
- The central chart is fast, native-feeling, and renders our pre-computed indicators + overlays directly; RSI/MACD subcharts are native panes.
- Clean separation: candlestick chart (library) · order-flow heatmap (bespoke canvas/WebGL) · other bespoke viz (d3).

**Negative / costs**
- We compute indicators ourselves — already the plan (the processor pre-computes them).
- **The order-flow / DOM heatmap is the hardest piece** — a Bookmap-class component is real R&D (canvas/WebGL, dense real-time data). Scope it deliberately; it lands with order-flow work (Phase 3) and is "an option," not a Phase-1 must.

## Follow-ups
- Prototype the Lightweight Charts pane setup for RSI/MACD from pre-computed series; define the overlay contract (indicator series, price lines, suggestion zones, position/fill markers).
- **Spike the DOM / order-flow renderer** (canvas vs. WebGL — e.g. PixiJS / regl) against `GatewayDepth` / `GatewayTrade` volumes (§12; Phase 3).
