import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Stub the chart (its own suite covers it, and it would open a real canvas) and the panel (its own suite covers the
// R-14 scoping). The chart stub reflects its props as data-attributes so the composition + retargeting are assertable.
vi.mock('../chart/MarketChart', () => ({
  MarketChart: (props: {
    venue: string;
    instrument: string;
    resolution: number;
    indicators?: readonly { indicator: string }[];
    levelTimeframes?: readonly number[];
    suggestionZones?: readonly { id: string }[];
    suggestionsStale?: boolean;
  }) => (
    <div
      data-testid="chart-stub"
      data-venue={props.venue}
      data-instrument={props.instrument}
      data-resolution={props.resolution}
      data-indicators={(props.indicators ?? []).map((spec) => spec.indicator).join(',')}
      data-level-timeframes={(props.levelTimeframes ?? []).join(',')}
      data-suggestion-zones={(props.suggestionZones ?? []).map((zone) => zone.id).join(',')}
      data-suggestions-stale={String(props.suggestionsStale ?? false)}
    />
  ),
}));
vi.mock('../suggestions/SuggestionsPanel', () => ({
  SuggestionsPanel: () => <div data-testid="panel-stub" />,
}));

// The zone hook has its own suite (it wires accounts + realtime); here it is driven to assert the zones + stale flag
// reach the chart.
const { useSuggestionZonesMock } = vi.hoisted(() => ({ useSuggestionZonesMock: vi.fn() }));
vi.mock('../chart/useSuggestionZones', () => ({ useSuggestionZones: useSuggestionZonesMock }));

import type { Destination } from '../navigation/destinations';
import { WorkspaceSurface } from './WorkspaceSurface';

const destination = { id: 'workspace', path: '/', label: 'Workspace' } as unknown as Destination;

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

beforeEach(() => {
  useSuggestionZonesMock.mockReturnValue({ zones: [], stale: false });
});

describe('WorkspaceSurface', () => {
  it('is the workspace surface, composing the central chart and the suggestions panel', () => {
    render(<WorkspaceSurface destination={destination} />);

    expect(screen.getByTestId('surface').dataset.surface).toBe('workspace');
    expect(screen.getByTestId('chart-stub')).toBeTruthy();
    expect(screen.getByTestId('panel-stub')).toBeTruthy();
  });

  it('drives the chart with the default venue / instrument / resolution', () => {
    render(<WorkspaceSurface destination={destination} />);

    const chart = screen.getByTestId('chart-stub');
    expect(chart.dataset.venue).toBe('projectx');
    expect(chart.dataset.instrument).toBe('ES');
    expect(chart.dataset.resolution).toBe('1');
  });

  it('retargets the chart when the operator submits a new instrument (upper-cased, on submit not per keystroke)', () => {
    render(<WorkspaceSurface destination={destination} />);
    const input = screen.getByLabelText('Instrument');

    fireEvent.change(input, { target: { value: 'nq' } });
    // Not yet — the chart still shows the old instrument until the form commits.
    expect(screen.getByTestId('chart-stub').dataset.instrument).toBe('ES');

    fireEvent.submit(input.closest('form') as HTMLFormElement);
    expect(screen.getByTestId('chart-stub').dataset.instrument).toBe('NQ');
  });

  it('retargets the chart when the operator picks a resolution', () => {
    render(<WorkspaceSurface destination={destination} />);

    fireEvent.change(screen.getByLabelText('Resolution'), { target: { value: '15' } });

    expect(screen.getByTestId('chart-stub').dataset.resolution).toBe('15');
  });

  it('shows RSI by default and toggles indicators onto the chart (gh#726)', () => {
    render(<WorkspaceSurface destination={destination} />);
    const indicators = () => screen.getByTestId('chart-stub').dataset.indicators;
    expect(indicators()).toBe('rsi'); // RSI on by default

    fireEvent.click(screen.getByRole('checkbox', { name: 'RSI' }));
    expect(indicators()).toBe(''); // toggled off

    fireEvent.click(screen.getByRole('checkbox', { name: 'ATR' }));
    expect(indicators()).toBe('atr'); // a second indicator toggled on
  });

  it('toggles the price-level overlay onto the chart at the current resolution (gh#727)', () => {
    render(<WorkspaceSurface destination={destination} />);
    const timeframes = () => screen.getByTestId('chart-stub').dataset.levelTimeframes;
    expect(timeframes()).toBe(''); // off by default — no level fetch until the operator asks

    fireEvent.click(screen.getByRole('checkbox', { name: 'Levels' }));
    expect(timeframes()).toBe('1'); // the chart's own timeframe (default 1m)

    fireEvent.change(screen.getByLabelText('Resolution'), { target: { value: '15' } });
    expect(timeframes()).toBe('15'); // follows the resolution while on
  });

  it('feeds the chart the active suggestion zones and their stale flag for the charted instrument (gh#727)', () => {
    useSuggestionZonesMock.mockReturnValue({
      zones: [{ id: 's1', entry: 5300, stop: 5290, target: 5320 }],
      stale: true,
    });

    render(<WorkspaceSurface destination={destination} />);

    expect(useSuggestionZonesMock).toHaveBeenCalledWith('ES'); // scoped to the charted instrument
    const chart = screen.getByTestId('chart-stub');
    expect(chart.dataset.suggestionZones).toBe('s1');
    expect(chart.dataset.suggestionsStale).toBe('true');
  });
});
