import { type ApiResult, requestJson } from './client';

/**
 * One OHLCV bar, as the market-data read serves it (gh#644). `bucketStart` is the bucket's open time (ISO-8601 UTC);
 * prices arrive as JSON numbers (the server's `decimal`s) — fine to *display*, never to size an order against.
 */
export interface BarPoint {
  readonly bucketStart: string;
  readonly open: number;
  readonly high: number;
  readonly low: number;
  readonly close: number;
  readonly volume: number;
}

/**
 * A bounded, ascending OHLCV series for one `(venue, instrument, resolution)` — the `/api/marketdata/bars` body
 * (gh#644). The spec does not type this anonymous response, so it is named here (the `api/client` convention).
 */
export interface BarSeries {
  readonly venue: string;
  readonly instrument: string;
  readonly resolutionMinutes: number;
  readonly bars: readonly BarPoint[];
}

/**
 * Reads OHLCV bars for a `(venue, instrument, resolution)` over a bounded `[from, to)` window (gh#644), through the
 * authenticated client (R-18 — never a raw `fetch`). The three degraded outcomes are distinct and the caller must
 * keep them so (R-19): a window wider than the server's cap is a **refusal** (`kind: 'refused'`), an unknown series
 * is a 404 **failure**, and a *known* series with no bars in the window is a success with an **empty** `bars`.
 */
export function getBars(
  venue: string,
  instrument: string,
  resolution: number,
  from: string,
  to: string,
): Promise<ApiResult<BarSeries>> {
  const query = new URLSearchParams({
    venue,
    instrument,
    resolution: String(resolution),
    from,
    to,
  });
  return requestJson<BarSeries>('GET', `/api/marketdata/bars?${query.toString()}`);
}

/** One value of a pre-computed indicator series (gh#644): the bucket's open time (ISO-8601 UTC) and the value. */
export interface IndicatorPoint {
  readonly bucketStart: string;
  readonly value: number;
}

/**
 * A bounded, ascending pre-computed indicator series for one `(venue, instrument, resolution, indicator, period)` —
 * the `/api/marketdata/indicators` body (gh#644). R-22: the value is the server's single number, never re-derived.
 */
export interface IndicatorSeries {
  readonly venue: string;
  readonly instrument: string;
  readonly resolutionMinutes: number;
  readonly indicator: string;
  readonly period: number;
  readonly points: readonly IndicatorPoint[];
}

/**
 * Reads a pre-computed indicator series over a bounded `[from, to)` window (gh#644), through the authenticated client
 * (R-18). The server serves only the indicators the projection computes (`atr`, `rsi`); an unknown one or a
 * non-positive `period` is a **refusal** (400 — a client mistake worth naming), a too-wide window a refusal, an
 * unknown series a failure, and a known series with the indicator not yet computed a success with empty `points` (R-19).
 */
export function getIndicators(
  venue: string,
  instrument: string,
  resolution: number,
  indicator: string,
  period: number,
  from: string,
  to: string,
): Promise<ApiResult<IndicatorSeries>> {
  const query = new URLSearchParams({
    venue,
    instrument,
    resolution: String(resolution),
    indicator,
    period: String(period),
    from,
    to,
  });
  return requestJson<IndicatorSeries>('GET', `/api/marketdata/indicators?${query.toString()}`);
}

/**
 * One active price level for the chart (gh#644): a support / resistance *band* (`top`..`bottom`) at a timeframe, with
 * its detector metadata. Prices arrive as JSON numbers (the server's `decimal`s) — fine to *draw*, never to size an
 * order against.
 */
export interface PriceLevel {
  readonly timeframeMinutes: number;
  readonly top: number;
  readonly bottom: number;
  /** The side of price. The server serves `Support` or `Resistance`; `Unknown` is the refusable zero, never sent. */
  readonly kind: string;
  readonly significance: number;
  readonly formedAtBucket: string;
  readonly touchCount: number;
}

/**
 * The active price levels for one `(venue, instrument)` across the requested timeframes — the `/api/marketdata/levels`
 * body (gh#644). Unlike bars / indicators this read is NOT windowed: it is the set of currently-active levels, not a
 * time series. The spec does not type this anonymous response, so it is named here (the `api/client` convention).
 */
export interface PriceLevels {
  readonly venue: string;
  readonly instrument: string;
  readonly levels: readonly PriceLevel[];
}

/**
 * Reads the active price levels for `(venue, instrument)` across one or more `timeframes` (minutes), through the
 * authenticated client (R-18 — never a raw `fetch`). Derived market data is global / shared (R-22), so there is no
 * owner filter. The degraded outcomes stay distinct (R-19): a missing / invalid venue or instrument, or an empty or
 * non-positive `timeframes` set, is a **refusal** (400); an instrument unknown to the market-data store is a 404
 * **failure**; a known instrument with no levels formed yet is a success with an **empty** `levels`. `timeframes`
 * binds as a REPEATED query param (the server's `int[]`), never a comma-joined list.
 */
export function getLevels(
  venue: string,
  instrument: string,
  timeframes: readonly number[],
): Promise<ApiResult<PriceLevels>> {
  const query = new URLSearchParams({ venue, instrument });
  timeframes.forEach((timeframe) => query.append('timeframes', String(timeframe)));
  return requestJson<PriceLevels>('GET', `/api/marketdata/levels?${query.toString()}`);
}
