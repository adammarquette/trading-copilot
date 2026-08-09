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

/** The wire shape from `/api/marketdata/bars` (`BarSeriesResponse`), mapped to {@link BarSeries} for callers. */
interface BarSeriesWire {
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
  return request<BarSeriesWire>('GET', `/api/marketdata/bars?${query.toString()}`).then((result) =>
    result.ok
      ? {
          ok: true,
          data: {
            venue: result.data.venue,
            instrument: result.data.instrument,
            resolution: result.data.resolutionMinutes,
            points: result.data.bars,
          },
        }
      : result,
  );
}
