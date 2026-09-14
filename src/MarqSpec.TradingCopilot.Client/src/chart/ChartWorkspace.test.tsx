import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Stub the chart (its own suite covers the canvas) — the stub reflects its props as data-attributes so the shared
// column's composition + retargeting are assertable without a real Lightweight-Charts canvas.
vi.mock('./MarketChart', () => ({
  MarketChart: (props: {
    venue: string;
    instrument: string;
    resolution: number;
    indicators?: readonly { indicator: string }[];
    levelTimeframes?: readonly number[];
    suggestionZones?: readonly { id: string }[];
    execution?: { orders: readonly { id: string }[]; position: { netQuantity: number } | null };
    fills?: readonly { id: string }[];
  }) => (
    <div
      data-testid="chart-stub"
      data-venue={props.venue}
      data-instrument={props.instrument}
      data-resolution={props.resolution}
      data-indicators={(props.indicators ?? []).map((spec) => spec.indicator).join(',')}
      data-level-timeframes={(props.levelTimeframes ?? []).join(',')}
      data-suggestion-zones={(props.suggestionZones ?? []).map((zone) => zone.id).join(',')}
      data-execution-orders={(props.execution?.orders ?? []).map((order) => order.id).join(',')}
      data-fills={(props.fills ?? []).map((fill) => fill.id).join(',')}
    />
  ),
}));

// The overlay hooks each have their own suite (accounts + realtime); here they are driven to assert the shared column
// scopes them to the instrument prop and threads their output to the chart.
const { useSuggestionZonesMock } = vi.hoisted(() => ({ useSuggestionZonesMock: vi.fn() }));
vi.mock('./useSuggestionZones', () => ({ useSuggestionZones: useSuggestionZonesMock }));
const { useExecutionOverlaysMock } = vi.hoisted(() => ({ useExecutionOverlaysMock: vi.fn() }));
vi.mock('./useExecutionOverlays', () => ({ useExecutionOverlays: useExecutionOverlaysMock }));
const { useFillMarkersMock } = vi.hoisted(() => ({ useFillMarkersMock: vi.fn() }));
vi.mock('./useFillMarkers', () => ({ useFillMarkers: useFillMarkersMock }));

import { ChartWorkspace } from './ChartWorkspace';

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

beforeEach(() => {
  useSuggestionZonesMock.mockReturnValue({ zones: [], stale: false });
  useExecutionOverlaysMock.mockReturnValue({
    overlay: { orders: [], position: null },
    stale: false,
    unavailable: false,
  });
  useFillMarkersMock.mockReturnValue({ fills: [], stale: false, unavailable: false });
});

describe('ChartWorkspace', () => {
  it('composes the controls and the chart, feeding the chart the given instrument at the default venue / resolution', () => {
    render(<ChartWorkspace instrument="ES" onInstrument={vi.fn()} />);

    const chart = screen.getByTestId('chart-stub');
    expect(chart.dataset.venue).toBe('projectx');
    expect(chart.dataset.instrument).toBe('ES');
    expect(chart.dataset.resolution).toBe('1');
    expect(screen.getByLabelText('Instrument')).toBeTruthy();
  });

  it('lifts a committed instrument to onInstrument (upper-cased, on submit — not per keystroke)', () => {
    const onInstrument = vi.fn();
    render(<ChartWorkspace instrument="ES" onInstrument={onInstrument} />);
    const input = screen.getByLabelText('Instrument');

    fireEvent.change(input, { target: { value: 'nq' } });
    expect(onInstrument).not.toHaveBeenCalled(); // typing alone does not commit

    fireEvent.submit(input.closest('form') as HTMLFormElement);
    expect(onInstrument).toHaveBeenCalledWith('NQ'); // committed, upper-cased — the parent owns the instrument
  });

  it('owns the resolution / indicators / levels view state locally', () => {
    render(<ChartWorkspace instrument="ES" onInstrument={vi.fn()} />);
    const chart = () => screen.getByTestId('chart-stub');

    expect(chart().dataset.indicators).toBe('rsi'); // RSI on by default
    fireEvent.click(screen.getByRole('checkbox', { name: 'ATR' }));
    expect(chart().dataset.indicators).toBe('rsi,atr');

    expect(chart().dataset.levelTimeframes).toBe(''); // levels off by default
    fireEvent.click(screen.getByRole('checkbox', { name: 'Levels' }));
    fireEvent.change(screen.getByLabelText('Resolution'), { target: { value: '15' } });
    expect(chart().dataset.resolution).toBe('15');
    expect(chart().dataset.levelTimeframes).toBe('15'); // levels follow the resolution while on
  });

  it('scopes the overlay hooks to the charted instrument and threads their output to the chart', () => {
    useSuggestionZonesMock.mockReturnValue({
      zones: [{ id: 's1', entry: 5300, stop: 5290, target: 5320 }],
      stale: false,
    });
    useExecutionOverlaysMock.mockReturnValue({
      overlay: { orders: [{ id: 'o1:stop' }], position: { averagePrice: 5300, netQuantity: 2 } },
      stale: false,
      unavailable: false,
    });
    useFillMarkersMock.mockReturnValue({
      fills: [{ id: 'f1' }],
      stale: false,
      unavailable: false,
    });

    render(<ChartWorkspace instrument="NQ" onInstrument={vi.fn()} />);

    expect(useSuggestionZonesMock).toHaveBeenCalledWith('NQ');
    expect(useExecutionOverlaysMock).toHaveBeenCalledWith('NQ');
    expect(useFillMarkersMock).toHaveBeenCalledWith('NQ');
    const chart = screen.getByTestId('chart-stub');
    expect(chart.dataset.suggestionZones).toBe('s1');
    expect(chart.dataset.executionOrders).toBe('o1:stop');
    expect(chart.dataset.fills).toBe('f1');
  });
});
