import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { FakeBroadcastChannel, installFakeBroadcastChannel } from '../testing/fakeBroadcastChannel';
import {
  LINKED_INSTRUMENT_CHANNEL,
  encodeInstrumentMessage,
  writeLinkedInstrumentSeed,
} from './linkedInstrument';

// Stub the chart (its own suite covers the canvas); the stub reflects its instrument so the linked retarget is
// assertable without a real Lightweight-Charts canvas.
vi.mock('../chart/MarketChart', () => ({
  MarketChart: (props: { instrument: string }) => (
    <div data-testid="chart-stub" data-instrument={props.instrument} />
  ),
}));

// The overlay hooks each have their own suite (accounts + realtime); driven to "nothing" here so the detached panel
// renders without the authenticated providers the real hooks need.
vi.mock('../chart/useSuggestionZones', () => ({
  useSuggestionZones: () => ({ zones: [], stale: false }),
}));
vi.mock('../chart/useExecutionOverlays', () => ({
  useExecutionOverlays: () => ({
    overlay: { orders: [], position: null },
    stale: false,
    unavailable: false,
  }),
}));
vi.mock('../chart/useFillMarkers', () => ({
  useFillMarkers: () => ({ fills: [], stale: false, unavailable: false }),
}));

import { DetachedChart } from './DetachedChart';

let restore: () => void;

beforeEach(() => {
  restore = installFakeBroadcastChannel();
});

afterEach(() => {
  cleanup();
  restore();
  FakeBroadcastChannel.reset();
  sessionStorage.clear();
  vi.clearAllMocks();
});

describe('DetachedChart', () => {
  it('renders the shared chart column, opening on the default linked instrument', () => {
    render(<DetachedChart />);

    expect(screen.getByTestId('chart-stub').dataset.instrument).toBe('ES');
    expect(screen.getByLabelText('Instrument')).toBeTruthy(); // the shared column's controls came along
  });

  it('follows the instrument another window selects over the linked channel', () => {
    // The docked workspace (another window) selecting NQ broadcasts it; the detached chart follows — neither window
    // is authoritative, the detached chart just mirrors the last write.
    const dockedWindow = new FakeBroadcastChannel(LINKED_INSTRUMENT_CHANNEL);
    render(<DetachedChart />);
    expect(screen.getByTestId('chart-stub').dataset.instrument).toBe('ES');

    act(() => dockedWindow.postMessage(encodeInstrumentMessage('NQ', 'docked-window-origin')));

    expect(screen.getByTestId('chart-stub').dataset.instrument).toBe('NQ');
  });

  it("opens on the seeded instrument — the opener window's last selection in sessionStorage (gh#1017)", () => {
    // A window.open()ed child inherits a COPY of the opener's sessionStorage at open, so a chart detached while the
    // docked window shows NQ opens on NQ, not the default — without either window authoring the other's state.
    writeLinkedInstrumentSeed('NQ');

    render(<DetachedChart />);

    expect(screen.getByTestId('chart-stub').dataset.instrument).toBe('NQ');
  });
});
