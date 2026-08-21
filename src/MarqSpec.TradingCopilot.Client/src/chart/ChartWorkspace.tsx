import Box from '@mui/material/Box';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import TextField from '@mui/material/TextField';
import { useCallback, useMemo, useState } from 'react';

import { MarketChart } from './MarketChart';
import type { IndicatorSpec } from './MarketChart';
import { useExecutionOverlays } from './useExecutionOverlays';
import { useFillMarkers } from './useFillMarkers';
import { useSuggestionZones } from './useSuggestionZones';

/**
 * The market-data venue the bars are keyed under (gh#644). A single-venue default for now; the operator picks the
 * instrument + resolution below. A real symbol search / active-instrument context lands with the trading surfaces.
 */
const DEFAULT_VENUE = 'projectx';

/** The instrument a chart opens on before the operator picks one (gh#644). The seed the owning window links from
 *  (gh#1015): the docked workspace and a detached chart both start here and then follow each other. */
export const DEFAULT_INSTRUMENT = 'ES';

/** The resolutions the operator can pick — the minute buckets the bar store serves (gh#644). */
const RESOLUTIONS: readonly { readonly minutes: number; readonly label: string }[] = [
  { minutes: 1, label: '1m' },
  { minutes: 5, label: '5m' },
  { minutes: 15, label: '15m' },
  { minutes: 60, label: '1h' },
];

/** The indicators the operator can toggle into their own pane — the ones the projection computes (gh#644: atr, rsi). */
const AVAILABLE_INDICATORS: readonly {
  readonly indicator: string;
  readonly period: number;
  readonly label: string;
  readonly color: string;
}[] = [
  { indicator: 'rsi', period: 14, label: 'RSI', color: '#f0b90b' },
  { indicator: 'atr', period: 14, label: 'ATR', color: '#4a90d9' },
];

/** A stable empty timeframe set for when the price-level overlay is off (gh#727) — a module const so the chart's
 * level fetch is not re-run by an unrelated render passing a fresh `[]`. */
const NO_TIMEFRAMES: readonly number[] = [];

export interface ChartWorkspaceProps {
  /** The charted instrument. Owned by the parent (the docked workspace or a detached chart) so it can be *linked*
   *  across windows (gh#1015); this column is a controlled view of it. */
  readonly instrument: string;
  /** Commit a new instrument up to the owner — the picker calls this on submit, upper-cased. */
  readonly onInstrument: (instrument: string) => void;
}

/**
 * The shared chart column (gh#1015): the candlestick chart plus its instrument / resolution / indicator controls and
 * the overlay hooks that keep suggestions, execution and fills on it. Extracted from `WorkspaceSurface` so the docked
 * workspace and the **detached chart pop-out** ({@link ../panels/DetachedChart}) render the *same* column with no JSX
 * duplication. The instrument is a controlled prop (the owner links it across windows); resolution, indicators and the
 * level overlay are this column's own view state — they are not linked, each window keeps its own.
 */
export function ChartWorkspace({
  instrument,
  onInstrument,
}: ChartWorkspaceProps): React.JSX.Element {
  const [resolution, setResolution] = useState(RESOLUTIONS[0].minutes);
  const [enabledIndicators, setEnabledIndicators] = useState<ReadonlySet<string>>(
    () => new Set(['rsi']),
  );
  const [showLevels, setShowLevels] = useState(false);

  // The price-level overlay draws the chart's own timeframe (the current resolution). A STABLE reference when off, so
  // toggling an indicator or retyping the instrument never re-runs the chart's level fetch. (gh#727)
  const levelTimeframes = useMemo<readonly number[]>(
    () => (showLevels ? [resolution] : NO_TIMEFRAMES),
    [showLevels, resolution],
  );

  // The active suggestion's entry / stop / target overlay the chart, kept fresh + owner-scoped by the hook (gh#727).
  const { zones: suggestionZones, stale: suggestionsStale } = useSuggestionZones(instrument);

  // The operator's live working orders + net position overlay the chart, kept fresh on every order-state / fill push
  // and owner-scoped by the hook (gh#727 increment 3, from the gh#772 venue-truth reads).
  const {
    overlay: execution,
    stale: executionStale,
    unavailable: executionUnavailable,
  } = useExecutionOverlays(instrument);

  // The operator's recent fills mark the chart, kept fresh on every fill push and owner-scoped by the hook (gh#727,
  // from the gh#792 journal read).
  const { fills, stale: fillsStale, unavailable: fillsUnavailable } = useFillMarkers(instrument);

  // A STABLE array so toggling an indicator (not the instrument/resolution) never re-runs the chart's bars fetch for
  // no reason; the chart adds/removes the pane in place rather than remounting.
  const indicators = useMemo<readonly IndicatorSpec[]>(
    () =>
      AVAILABLE_INDICATORS.filter((option) => enabledIndicators.has(option.indicator)).map(
        (option) => ({
          indicator: option.indicator,
          period: option.period,
          color: option.color,
        }),
      ),
    [enabledIndicators],
  );

  const toggleIndicator = useCallback((indicator: string) => {
    setEnabledIndicators((current) => {
      const next = new Set(current);
      if (next.has(indicator)) {
        next.delete(indicator);
      } else {
        next.add(indicator);
      }
      return next;
    });
  }, []);

  const toggleLevels = useCallback(() => setShowLevels((current) => !current), []);

  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <ChartControls
        instrument={instrument}
        resolution={resolution}
        enabledIndicators={enabledIndicators}
        showLevels={showLevels}
        onInstrument={onInstrument}
        onResolution={setResolution}
        onToggleIndicator={toggleIndicator}
        onToggleLevels={toggleLevels}
      />
      <Box sx={{ flex: 1, minHeight: 0 }}>
        {/* Key on the series identity so a change remounts the chart (fresh loading + fetch, no in-effect setState).
            Indicators are NOT in the key — they add / remove their pane in place. */}
        <MarketChart
          key={`${DEFAULT_VENUE}:${instrument}:${resolution}`}
          venue={DEFAULT_VENUE}
          instrument={instrument}
          resolution={resolution}
          indicators={indicators}
          levelTimeframes={levelTimeframes}
          suggestionZones={suggestionZones}
          suggestionsStale={suggestionsStale}
          execution={execution}
          executionStale={executionStale}
          executionUnavailable={executionUnavailable}
          fills={fills}
          fillsStale={fillsStale}
          fillsUnavailable={fillsUnavailable}
        />
      </Box>
    </Box>
  );
}

interface ChartControlsProps {
  readonly instrument: string;
  readonly resolution: number;
  readonly enabledIndicators: ReadonlySet<string>;
  readonly showLevels: boolean;
  readonly onInstrument: (instrument: string) => void;
  readonly onResolution: (resolution: number) => void;
  readonly onToggleIndicator: (indicator: string) => void;
  readonly onToggleLevels: () => void;
}

/**
 * The instrument + resolution picker. The instrument **commits on submit** (Enter) so the chart does not refetch on
 * every keystroke; the resolution commits on select. A native select keeps it operable and testable without a menu.
 */
function ChartControls({
  instrument,
  resolution,
  enabledIndicators,
  showLevels,
  onInstrument,
  onResolution,
  onToggleIndicator,
  onToggleLevels,
}: ChartControlsProps): React.JSX.Element {
  const [draft, setDraft] = useState(instrument);

  return (
    <Box
      component="form"
      onSubmit={(event) => {
        event.preventDefault();
        const next = draft.trim().toUpperCase();
        if (next.length > 0) {
          onInstrument(next);
        }
      }}
      sx={{ display: 'flex', flexWrap: 'wrap', gap: 1, alignItems: 'center', px: 2, py: 1 }}
    >
      <TextField
        label="Instrument"
        size="small"
        value={draft}
        onChange={(event) => setDraft(event.target.value)}
        sx={{ width: 140 }}
      />
      <TextField
        select
        label="Resolution"
        size="small"
        value={String(resolution)}
        onChange={(event) => onResolution(Number(event.target.value))}
        slotProps={{ select: { native: true } }}
        sx={{ width: 120 }}
      >
        {RESOLUTIONS.map((option) => (
          <option key={option.minutes} value={option.minutes}>
            {option.label}
          </option>
        ))}
      </TextField>

      {AVAILABLE_INDICATORS.map((option) => (
        <FormControlLabel
          key={option.indicator}
          control={
            <Checkbox
              size="small"
              checked={enabledIndicators.has(option.indicator)}
              onChange={() => onToggleIndicator(option.indicator)}
            />
          }
          label={option.label}
        />
      ))}
      <FormControlLabel
        control={<Checkbox size="small" checked={showLevels} onChange={onToggleLevels} />}
        label="Levels"
      />
    </Box>
  );
}
