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
    execution?: {
      orders: readonly { id: string }[];
      position: { netQuantity: number } | null;
    };
    executionStale?: boolean;
    executionUnavailable?: boolean;
    fills?: readonly { id: string }[];
    fillsStale?: boolean;
    fillsUnavailable?: boolean;
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
      data-execution-orders={(props.execution?.orders ?? []).map((order) => order.id).join(',')}
      data-execution-position={
        props.execution?.position ? String(props.execution.position.netQuantity) : ''
      }
      data-execution-stale={String(props.executionStale ?? false)}
      data-execution-unavailable={String(props.executionUnavailable ?? false)}
      data-fills={(props.fills ?? []).map((fill) => fill.id).join(',')}
      data-fills-stale={String(props.fillsStale ?? false)}
      data-fills-unavailable={String(props.fillsUnavailable ?? false)}
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

// The execution-overlay hook has its own suite (accounts + realtime); here it is driven to assert the overlay + stale
// flag reach the chart (gh#727 increment 3).
const { useExecutionOverlaysMock } = vi.hoisted(() => ({ useExecutionOverlaysMock: vi.fn() }));
vi.mock('../chart/useExecutionOverlays', () => ({
  useExecutionOverlays: useExecutionOverlaysMock,
}));

// The fill-marker hook has its own suite (accounts + realtime); here it is driven to assert the marks + flags reach
// the chart (gh#727).
const { useFillMarkersMock } = vi.hoisted(() => ({ useFillMarkersMock: vi.fn() }));
vi.mock('../chart/useFillMarkers', () => ({ useFillMarkers: useFillMarkersMock }));

// The blotter has its own suite (venue-truth bases, protection, the cancel confirmation); here it is stubbed to
// assert only that the workspace mounts it and hands it the ACTIVE account.
vi.mock('../blotter/Blotter', () => ({
  Blotter: (props: { accountId: string }) => (
    <div data-testid="blotter-stub" data-account={props.accountId} />
  ),
}));

const { useOptionalAccountsMock } = vi.hoisted(() => ({ useOptionalAccountsMock: vi.fn() }));
vi.mock('../accounts/AccountProvider', () => ({ useOptionalAccounts: useOptionalAccountsMock }));

import type { Destination } from '../navigation/destinations';
import { WorkspaceSurface } from './WorkspaceSurface';

const destination = { id: 'workspace', path: '/', label: 'Workspace' } as unknown as Destination;

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
  useOptionalAccountsMock.mockReturnValue({
    status: 'ready',
    activeAccount: { id: 'acct-1' },
  });
});

describe('WorkspaceSurface live blotter', () => {
  it('mounts the blotter for the active account', () => {
    // gh#656. The destination's own note said the blotter was "still its own card"; this is that card landing.
    render(<WorkspaceSurface destination={destination} />);

    expect(screen.getByTestId('blotter-stub').getAttribute('data-account')).toBe('acct-1');
  });

  it('retargets the blotter when the operator switches account', () => {
    const { rerender } = render(<WorkspaceSurface destination={destination} />);
    useOptionalAccountsMock.mockReturnValue({
      status: 'ready',
      activeAccount: { id: 'acct-2' },
    });

    rerender(<WorkspaceSurface destination={destination} />);

    expect(screen.getByTestId('blotter-stub').getAttribute('data-account')).toBe('acct-2');
  });

  it('shows no blotter — and nothing implying flat — before an account resolves', () => {
    // The same discipline the blotter itself holds: "no account yet" is not "no positions". Rendering an empty
    // blotter here would state something about venue truth that has not been read.
    useOptionalAccountsMock.mockReturnValue({ status: 'loading' });

    render(<WorkspaceSurface destination={destination} />);

    expect(screen.queryByTestId('blotter-stub')).toBeNull();
  });

  it('shows no blotter outside an account provider', () => {
    useOptionalAccountsMock.mockReturnValue(null);

    render(<WorkspaceSurface destination={destination} />);

    expect(screen.queryByTestId('blotter-stub')).toBeNull();
  });
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

  it('feeds the chart the operator execution overlay and its stale flag for the charted instrument (gh#727 increment 3)', () => {
    useExecutionOverlaysMock.mockReturnValue({
      overlay: {
        orders: [{ id: 'o1:stop', price: 5290, kind: 'stop', size: 2 }],
        position: { averagePrice: 5300, netQuantity: 2 },
      },
      stale: true,
      unavailable: true,
    });

    render(<WorkspaceSurface destination={destination} />);

    expect(useExecutionOverlaysMock).toHaveBeenCalledWith('ES'); // scoped to the charted instrument
    const chart = screen.getByTestId('chart-stub');
    expect(chart.dataset.executionOrders).toBe('o1:stop');
    expect(chart.dataset.executionPosition).toBe('2');
    expect(chart.dataset.executionStale).toBe('true');
    expect(chart.dataset.executionUnavailable).toBe('true');
  });

  it('feeds the chart the operator fill markers and their flags for the charted instrument (gh#727)', () => {
    useFillMarkersMock.mockReturnValue({
      fills: [{ id: 'f1', time: 1_800_000_000, side: 'Buy', size: 2 }],
      stale: true,
      unavailable: true,
    });

    render(<WorkspaceSurface destination={destination} />);

    expect(useFillMarkersMock).toHaveBeenCalledWith('ES'); // scoped to the charted instrument
    const chart = screen.getByTestId('chart-stub');
    expect(chart.dataset.fills).toBe('f1');
    expect(chart.dataset.fillsStale).toBe('true');
    expect(chart.dataset.fillsUnavailable).toBe('true');
  });
});
