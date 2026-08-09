import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

// Lightweight Charts is a canvas library jsdom cannot render, so mock it at its seam and assert on the calls the
// component makes (the same posture as mocking @microsoft/signalr for the realtime client). __mock exposes the spies.
const { chartMock } = vi.hoisted(() => {
  const setData = vi.fn();
  const remove = vi.fn();
  const fitContent = vi.fn();
  const addSeries = vi.fn(() => ({ setData }));
  const createChart = vi.fn(() => ({ addSeries, remove, timeScale: () => ({ fitContent }) }));
  return { chartMock: { setData, remove, fitContent, addSeries, createChart } };
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
function barsOk(points: ReturnType<typeof bar>[]) {
  return {
    ok: true as const,
    data: { venue: 'topstepx', instrument: 'ES', resolution: 1, points },
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

  it('tears the chart down on unmount — no leaked canvas', async () => {
    getBarsMock.mockResolvedValue(barsOk([bar('2026-01-01T00:00:00Z', 5300)]));

    const { unmount } = render(<MarketChart venue="topstepx" instrument="ES" resolution={1} />);
    await waitFor(() => expect(chartMock.setData).toHaveBeenCalled());

    unmount();

    expect(chartMock.remove).toHaveBeenCalledOnce();
  });
});
