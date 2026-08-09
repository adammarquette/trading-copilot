import Box from '@mui/material/Box';
import { useTheme } from '@mui/material/styles';
import { CandlestickSeries, createChart } from 'lightweight-charts';
import type { CandlestickData, IChartApi, ISeriesApi, UTCTimestamp } from 'lightweight-charts';
import { useEffect, useMemo, useRef, useState } from 'react';

import type { BarPoint, BarSeries } from '../api/marketData';
import { getBars } from '../api/marketData';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';

/** How far back the initial window reaches, in minutes. A follow-up (gh#726/#727) adds paging + live tick updates. */
const DEFAULT_LOOKBACK_MINUTES = 60 * 24 * 5;

type ChartState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly series: BarSeries }
  /** A *known* series with no bars in the window — a real answer (R-19), distinct from an error or an unknown series. */
  | { readonly status: 'empty' }
  /** A refusal (e.g. window too wide) or a failure (network, 5xx, unknown series) — shown, never a blank chart. */
  | { readonly status: 'error'; readonly message: string };

export interface MarketChartProps {
  readonly venue: string;
  readonly instrument: string;
  readonly resolution: number;
  /** The initial lookback window in minutes; injectable so a test needs no clock. */
  readonly lookbackMinutes?: number;
  /** The moment "now" resolves to for the window; injectable for the same reason. Defaults to the wall clock. */
  readonly now?: () => number;
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

/**
 * The central candlestick chart (gh#725, R-10, ADR-0004) — TradingView Lightweight Charts over the pre-computed
 * bars the BFF serves (gh#644), fetched through the authenticated client (R-18).
 *
 * <b>Honest states over a confident blank (R-19, ADR-0013):</b> a degraded read is *shown* — a window wider than the
 * server's cap and an unknown series surface as an error, an in-range gap as "no bars", loading as a spinner — never
 * a chart drawn empty as though the market went silent. The chart instance is created once and torn down on unmount
 * (no leaked canvas / `ResizeObserver`). Live tick updates and indicator panes / overlays are their own cards
 * (gh#726, gh#727); this renders the historical candles and the resolution it was handed.
 */
export function MarketChart({
  venue,
  instrument,
  resolution,
  lookbackMinutes = DEFAULT_LOOKBACK_MINUTES,
  now = Date.now,
}: MarketChartProps): React.JSX.Element {
  const theme = useTheme();
  const [state, setState] = useState<ChartState>({ status: 'loading' });

  const containerRef = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const seriesRef = useRef<ISeriesApi<'Candlestick'> | null>(null);

  // Fetch the window once per mount. The workspace keys this component on `(venue, instrument, resolution)`, so a
  // change to any of them remounts it — the initial `loading` state resets for free, with no synchronous setState in
  // the effect (React's reset-on-key pattern). `active` still guards a resolve landing after an unmount.
  useEffect(() => {
    let active = true;

    const nowMs = now();
    const to = new Date(nowMs).toISOString();
    const from = new Date(nowMs - lookbackMinutes * 60_000).toISOString();

    void getBars(venue, instrument, resolution, from, to).then((result) => {
      if (!active) {
        return;
      }
      if (result.ok) {
        setState(
          result.data.points.length === 0
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
  }, [venue, instrument, resolution, lookbackMinutes, now]);

  // Create the chart once, tear it down on unmount. Kept out of the fetch effect so a re-fetch never re-creates it.
  useEffect(() => {
    if (containerRef.current === null) {
      return;
    }
    const chart: IChartApi = createChart(containerRef.current, {
      autoSize: true,
      layout: { background: { color: 'transparent' }, textColor: theme.palette.text.secondary },
      grid: {
        vertLines: { color: theme.palette.divider },
        horzLines: { color: theme.palette.divider },
      },
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
  }, [theme.palette.text.secondary, theme.palette.divider]);

  // Push the candles onto the series whenever a ready window arrives.
  const candles = useMemo(
    () => (state.status === 'ready' ? toCandles(state.series.points) : null),
    [state],
  );
  useEffect(() => {
    if (candles !== null) {
      seriesRef.current?.setData(candles);
      chartRef.current?.timeScale().fitContent();
    }
  }, [candles]);

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
            description={`${instrument} has no ${resolution}-minute bars in the last few days.`}
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
