import { type ApiResult, request } from './client';

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
  return request<BarSeries>('GET', `/api/marketdata/bars?${query.toString()}`);
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
  return request<IndicatorSeries>('GET', `/api/marketdata/indicators?${query.toString()}`);
}
