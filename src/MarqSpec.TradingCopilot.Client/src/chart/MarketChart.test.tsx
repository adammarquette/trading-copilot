import { ThemeProvider, createTheme } from '@mui/material/styles';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

// Lightweight Charts is a canvas library jsdom cannot render, so mock it at its seam and assert on the calls the
// component makes (the same posture as mocking @microsoft/signalr for the realtime client). __mock exposes the spies.
const { chartMock } = vi.hoisted(() => {
  const setData = vi.fn();
  const remove = vi.fn();
  const fitContent = vi.fn();
  const applyOptions = vi.fn();
  const addSeries = vi.fn(() => ({ setData }));
  const createChart = vi.fn(() => ({
    addSeries,
    remove,
    applyOptions,
    timeScale: () => ({ fitContent }),
  }));
  return { chartMock: { setData, remove, fitContent, applyOptions, addSeries, createChart } };
});
vi.mock('lightweight-charts', () => ({
  createChart: chartMock.createChart,
  CandlestickSeries: 'Candlestick',
}));

const { getBarsMock } = vi.hoisted(() => ({ getBarsMock: vi.fn() }));
vi.mock('../api/marketData', () => ({ getBars: getBarsMock }));

import { MarketChart } from './MarketChart';

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function bar(bucketStart: string, close: number) {
  return { bucketStart, open: close, high: close + 1, low: close - 1, close, volume: 10 };
}
function barsOk(bars: ReturnType<typeof bar>[]) {
  return {
    ok: true as const,
    data: { venue: 'topstepx', instrument: 'ES', resolutionMinutes: 1, bars },
  };
}

describe('MarketChart', () => {
  it('fetches the window and draws the candles, mapping bucketStart to a UNIX-second time', async () => {
    getBarsMock.mockResolvedValue(
      barsOk([bar('2026-01-01T00:00:00Z', 5300), bar('2026-01-01T00:01:00Z', 5305)]),
    );

    render(
      <MarketChart
        venue="topstepx"
        instrument="ES"
        resolution={1}
        now={() => Date.parse('2026-01-02T00:00:00Z')}
      />,
    );

    expect(getBarsMock).toHaveBeenCalledWith(
      'topstepx',
      'ES',
      1,
      expect.any(String),
      '2026-01-02T00:00:00.000Z',
    );
    expect(chartMock.createChart).toHaveBeenCalledOnce(); // created once, not per fetch

    await waitFor(() => expect(chartMock.setData).toHaveBeenCalledOnce());
    const candles = chartMock.setData.mock.calls[0][0] as Array<{
      time: number;
      open: number;
      close: number;
    }>;
    expect(candles).toHaveLength(2);
    expect(candles[0]).toMatchObject({
      time: Math.floor(Date.parse('2026-01-01T00:00:00Z') / 1000),
      open: 5300,
      close: 5300,
    });
    expect(chartMock.fitContent).toHaveBeenCalled();
  });

  it('shows "no bars" for a known series with an empty window, and does not draw a thing', async () => {
    getBarsMock.mockResolvedValue(barsOk([]));

    render(<MarketChart venue="topstepx" instrument="ES" resolution={1} />);

    expect(await screen.findByText('No bars in this window')).toBeTruthy();
    expect(chartMock.setData).not.toHaveBeenCalled(); // an empty window is not an empty chart drawn confidently
  });

  it('shows the refusal reason instead of a blank chart (R-19)', async () => {
    getBarsMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 400,
      reason: 'window too wide',
    });

    render(<MarketChart venue="topstepx" instrument="ES" resolution={1} />);

    expect(await screen.findByText('Market data unavailable')).toBeTruthy();
    expect(screen.getByText('window too wide')).toBeTruthy();
    expect(chartMock.setData).not.toHaveBeenCalled();
  });

  it('shows a failure message on a failed read (unknown series / network / 5xx)', async () => {
    getBarsMock.mockResolvedValue({
      ok: false,
      kind: 'failed',
      status: 500,
      error: 'The request failed (500).',
    });

    render(<MarketChart venue="topstepx" instrument="ES" resolution={1} />);

    expect(await screen.findByText('The request failed (500).')).toBeTruthy();
  });

  it('recolors in place on a theme toggle, never tearing the chart down and blanking it (R-19)', async () => {
    // The regression for the review's 🔴 finding. A light/dark toggle rebuilds the MUI theme object
    // (ThemeModeProvider), so the palette values the chart reads change. If that tore the chart down and recreated
    // it, the already-loaded candles would be gone and the freshly created series would render empty until the next
    // unrelated fetch — the exact "confident blank" this component's own doc comment promises never happens.
    getBarsMock.mockResolvedValue(barsOk([bar('2026-01-01T00:00:00Z', 5300)]));
    const dark = createTheme({ palette: { mode: 'dark' } });
    const light = createTheme({ palette: { mode: 'light' } });

    const { rerender } = render(
      <ThemeProvider theme={dark}>
        <MarketChart venue="topstepx" instrument="ES" resolution={1} />
      </ThemeProvider>,
    );
    await waitFor(() => expect(chartMock.setData).toHaveBeenCalledOnce());

    rerender(
      <ThemeProvider theme={light}>
        <MarketChart venue="topstepx" instrument="ES" resolution={1} />
      </ThemeProvider>,
    );

    // Created once and recolored in place: the series (and its candles) is never destroyed by a toggle.
    expect(chartMock.createChart).toHaveBeenCalledOnce();
    expect(chartMock.remove).not.toHaveBeenCalled();
    expect(chartMock.applyOptions).toHaveBeenCalled();
  });

  it('sizes the default window under the server row cap at every resolution', async () => {
    // The regression for the review's 🟠 finding. A flat 5-day default lookback is ~7200 bars at 1-minute
    // resolution, past the server's 5000-row cap (MarketDataReadOptions.MaxRows) — so the DEFAULT view (no
    // lookbackMinutes prop) opened onto a "window too wide" refusal instead of a chart. The default now scales with
    // resolution so the implied bar count stays under the cap whatever the operator picks first.
    const SERVER_ROW_CAP = 5000;
    const nowMs = Date.parse('2026-01-10T00:00:00Z');

    for (const resolution of [1, 5, 15, 60]) {
      getBarsMock.mockClear();
      getBarsMock.mockResolvedValue(barsOk([]));

      render(
        <MarketChart venue="topstepx" instrument="ES" resolution={resolution} now={() => nowMs} />,
      );

      const [, , , from, to] = getBarsMock.mock.calls[0] as [
        string,
        string,
        number,
        string,
        string,
      ];
      const windowMinutes = (Date.parse(to) - Date.parse(from)) / 60_000;
      const impliedBars = windowMinutes / resolution;
      expect(impliedBars).toBeLessThan(SERVER_ROW_CAP);

      cleanup();
    }
  });

  it('tears the chart down on unmount — no leaked canvas', async () => {
    getBarsMock.mockResolvedValue(barsOk([bar('2026-01-01T00:00:00Z', 5300)]));

    const { unmount } = render(<MarketChart venue="topstepx" instrument="ES" resolution={1} />);
    await waitFor(() => expect(chartMock.setData).toHaveBeenCalled());

    unmount();

    expect(chartMock.remove).toHaveBeenCalledOnce();
  });
});
