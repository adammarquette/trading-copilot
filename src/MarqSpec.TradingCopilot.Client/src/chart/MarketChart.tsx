import Box from '@mui/material/Box';
import { useTheme } from '@mui/material/styles';
import { CandlestickSeries, LineSeries, createChart } from 'lightweight-charts';
import type {
  CandlestickData,
  IChartApi,
  ISeriesApi,
  LineData,
  UTCTimestamp,
} from 'lightweight-charts';
import { useEffect, useMemo, useRef, useState } from 'react';

import type { BarPoint, BarSeries, IndicatorPoint } from '../api/marketData';
import { getBars, getIndicators } from '../api/marketData';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';

/**
 * How many bars the default window aims for. Kept comfortably under the server's row cap
 * (`MarketDataReadOptions.MaxRows` = 5000, which refuses a wider window outright rather than truncating), so the
 * default view opens onto a chart, never a "window too wide" refusal. A follow-up (gh#726/#727) adds paging + live
 * tick updates. (gh#725 review.)
 */
const DEFAULT_TARGET_BARS = 4000;

/**
 * The default lookback for a resolution, in minutes. Sized so the window holds at most {@link DEFAULT_TARGET_BARS}
 * buckets — a window of `resolution × N` minutes yields at most `N` bars — so the implied bar count stays under the
 * server cap at every resolution. A flat 5-day constant was ~7200 bars at 1-minute resolution and tripped the cap.
 */
function defaultLookbackMinutes(resolution: number): number {
  return resolution * DEFAULT_TARGET_BARS;
}

type ChartState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly series: BarSeries }
  /** A *known* series with no bars in the window — a real answer (R-19), distinct from an error or an unknown series. */
  | { readonly status: 'empty' }
  /** A refusal (e.g. window too wide) or a failure (network, 5xx, unknown series) — shown, never a blank chart. */
  | { readonly status: 'error'; readonly message: string };

/** An indicator pane to render below the candles (gh#726): the pre-computed series name, its period, and a colour. */
export interface IndicatorSpec {
  readonly indicator: string;
  readonly period: number;
  readonly color?: string;
}

export interface MarketChartProps {
  readonly venue: string;
  readonly instrument: string;
  readonly resolution: number;
  /** The initial lookback window in minutes; injectable so a test needs no clock. */
  readonly lookbackMinutes?: number;
  /** The moment "now" resolves to for the window; injectable for the same reason. Defaults to the wall clock. */
  readonly now?: () => number;
  /** Indicator panes to render below the candles (gh#726), each fed by `/api/marketdata/indicators` (R-22). */
  readonly indicators?: readonly IndicatorSpec[];
}

/** Maps the server's OHLCV bars to Lightweight-Charts candles: `bucketStart` (ISO) → a UNIX-second `UTCTimestamp`. */
function toCandles(points: readonly BarPoint[]): CandlestickData<UTCTimestamp>[] {
  return points.map((point) => ({
    time: Math.floor(Date.parse(point.bucketStart) / 1000) as UTCTimestamp,
    open: point.open,
    high: point.high,
    low: point.low,
    close: point.close,
  }));
}

/** Maps a pre-computed indicator series to Lightweight-Charts line data (gh#726): `bucketStart` → a UNIX-second time. */
function toIndicatorLine(points: readonly IndicatorPoint[]): LineData<UTCTimestamp>[] {
  return points.map((point) => ({
    time: Math.floor(Date.parse(point.bucketStart) / 1000) as UTCTimestamp,
    value: point.value,
  }));
}

/**
 * The central candlestick chart (gh#725, R-10, ADR-0004) — TradingView Lightweight Charts over the pre-computed
 * bars the BFF serves (gh#644), fetched through the authenticated client (R-18).
 *
 * <b>Honest states over a confident blank (R-19, ADR-0013):</b> a degraded read is *shown* — a window wider than the
 * server's cap and an unknown series surface as an error, an in-range gap as "no bars", loading as a spinner — never
 * a chart drawn empty as though the market went silent. The chart instance is created once and torn down on unmount
 * (no leaked canvas / `ResizeObserver`). Indicator panes (gh#726) render below the candles from the pre-computed
 * series (R-22); live tick updates and the price overlays are their own cards (gh#649 / gh#727).
 */
export function MarketChart({
  venue,
  instrument,
  resolution,
  lookbackMinutes,
  now = Date.now,
  indicators = [],
}: MarketChartProps): React.JSX.Element {
  // Default the window from the resolution, not a flat constant, so it stays under the server's row cap whatever
  // resolution the workspace opens on (gh#725 review).
  const effectiveLookback = lookbackMinutes ?? defaultLookbackMinutes(resolution);
  const theme = useTheme();
  const [state, setState] = useState<ChartState>({ status: 'loading' });

  // The `[from, to)` window, computed ONCE on mount (a useState initializer, not a render-time clock read) and shared
  // by the bars and every indicator series so they align on the same axis. The workspace keys this component on the
  // series identity, so a change remounts it and recomputes the window.
  const [chartWindow] = useState(() => {
    const nowMs = now();
    return {
      from: new Date(nowMs - effectiveLookback * 60_000).toISOString(),
      to: new Date(nowMs).toISOString(),
    };
  });

  const containerRef = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const seriesRef = useRef<ISeriesApi<'Candlestick'> | null>(null);

  // Fetch the bars once per mount. The workspace keys this component on `(venue, instrument, resolution)`, so a change
  // remounts it — the initial `loading` state resets for free, no synchronous setState in the effect (React's
  // reset-on-key pattern). `active` still guards a resolve landing after an unmount.
  useEffect(() => {
    let active = true;

    void getBars(venue, instrument, resolution, chartWindow.from, chartWindow.to).then((result) => {
      if (!active) {
        return;
      }
      if (result.ok) {
        setState(
          result.data.bars.length === 0
            ? { status: 'empty' }
            : { status: 'ready', series: result.data },
        );
      } else {
        setState({
          status: 'error',
          message: result.kind === 'refused' ? result.reason : result.error,
        });
      }
    });

    return () => {
      active = false;
    };
  }, [venue, instrument, resolution, chartWindow]);

  // Create the chart ONCE, tear it down on unmount. Kept out of the fetch effect so a re-fetch never re-creates it,
  // and — deliberately — free of any theme dependency: a light/dark toggle must NOT recreate the chart, because that
  // would destroy the loaded series and leave the freshly created one blank until the next unrelated fetch (the
  // "confident blank" this component promises never to show). The palette-derived colours are set by the re-theme
  // effect below, which runs on mount as well, so there is no unthemed frame.
  useEffect(() => {
    if (containerRef.current === null) {
      return;
    }
    const chart: IChartApi = createChart(containerRef.current, {
      autoSize: true,
      layout: { background: { color: 'transparent' } },
      timeScale: { timeVisible: true, secondsVisible: false },
    });
    chartRef.current = chart;
    seriesRef.current = chart.addSeries(CandlestickSeries, {
      upColor: '#26a69a',
      downColor: '#ef5350',
      wickUpColor: '#26a69a',
      wickDownColor: '#ef5350',
      borderVisible: false,
    });
    return () => {
      chart.remove();
      chartRef.current = null;
      seriesRef.current = null;
    };
  }, []);

  // Colour the chart from the theme, and RE-colour it in place on a light/dark toggle — recolour, never recreate.
  // Running on mount too means the create effect above needs no theme dependency; running on every palette change is
  // what keeps the loaded candles on screen across a toggle instead of blanking them.
  useEffect(() => {
    chartRef.current?.applyOptions({
      layout: { textColor: theme.palette.text.secondary },
      grid: {
        vertLines: { color: theme.palette.divider },
        horzLines: { color: theme.palette.divider },
      },
    });
  }, [theme.palette.text.secondary, theme.palette.divider]);

  // Push the candles onto the series whenever a ready window arrives.
  const candles = useMemo(
    () => (state.status === 'ready' ? toCandles(state.series.bars) : null),
    [state],
  );
  useEffect(() => {
    if (candles !== null) {
      seriesRef.current?.setData(candles);
      chartRef.current?.timeScale().fitContent();
    }
  }, [candles]);

  // Indicator panes (gh#726): one line series per indicator, each in its own pane BELOW the candles (pane 0), fed by
  // the pre-computed series (R-22 — never recomputed here). Added dynamically (not via the mount key) so toggling an
  // indicator does not refetch the candles. `chart` is captured so a resolve/cleanup only touches series while THIS
  // chart is still current — a theme-driven recreation already disposed the old one with its panes.
  useEffect(() => {
    const chart = chartRef.current;
    if (chart === null || indicators.length === 0) {
      return;
    }
    let active = true;
    const added: ISeriesApi<'Line'>[] = [];

    void Promise.all(
      indicators.map((spec, index) =>
        getIndicators(
          venue,
          instrument,
          resolution,
          spec.indicator,
          spec.period,
          chartWindow.from,
          chartWindow.to,
        ).then((result) => {
          if (!active || chartRef.current !== chart || !result.ok) {
            return;
          }
          const line = chart.addSeries(
            LineSeries,
            {
              color: spec.color ?? '#f0b90b',
              lineWidth: 2,
              priceLineVisible: false,
              lastValueVisible: false,
            },
            index + 1, // pane 0 is the candles; each indicator gets the next pane down
          );
          line.setData(toIndicatorLine(result.data.points));
          added.push(line);
        }),
      ),
    );

    return () => {
      active = false;
      if (chartRef.current === chart) {
        added.forEach((line) => chart.removeSeries(line));
      }
    };
  }, [indicators, venue, instrument, resolution, chartWindow]);

  return (
    <Box data-testid="market-chart" sx={{ position: 'relative', height: '100%', minHeight: 240 }}>
      <Box ref={containerRef} sx={{ position: 'absolute', inset: 0 }} />
      {state.status === 'loading' ? (
        <Overlay>
          <LoadingState label={`Loading ${instrument}`} />
        </Overlay>
      ) : null}
      {state.status === 'empty' ? (
        <Overlay>
          <EmptyState
            title="No bars in this window"
            description={`${instrument} has no ${resolution}-minute bars in the selected window.`}
            tag="R-10"
          />
        </Overlay>
      ) : null}
      {state.status === 'error' ? (
        <Overlay>
          <EmptyState title="Market data unavailable" description={state.message} tag="R-19" />
        </Overlay>
      ) : null}
    </Box>
  );
}

/** A centered layer over the chart canvas for the loading / empty / error states. */
function Overlay({ children }: { readonly children: React.ReactNode }): React.JSX.Element {
  return (
    <Box
      sx={{
        position: 'absolute',
        inset: 0,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        bgcolor: 'background.default',
      }}
    >
      {children}
    </Box>
  );
}
