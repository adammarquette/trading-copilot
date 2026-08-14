import Box from '@mui/material/Box';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import TextField from '@mui/material/TextField';
import { useCallback, useMemo, useState } from 'react';

import { MarketChart } from '../chart/MarketChart';
import type { IndicatorSpec } from '../chart/MarketChart';
import { useExecutionOverlays } from '../chart/useExecutionOverlays';
import { useFillMarkers } from '../chart/useFillMarkers';
import { useSuggestionZones } from '../chart/useSuggestionZones';
import type { Destination } from '../navigation/destinations';
import { useOptionalAccounts } from '../accounts/AccountProvider';
import { Blotter } from '../blotter/Blotter';
import { SuggestionsPanel } from '../suggestions/SuggestionsPanel';

/**
 * The market-data venue the bars are keyed under (gh#644). A single-venue default for now; the operator picks the
 * instrument + resolution below. A real symbol search / active-instrument context lands with the trading surfaces.
 */
const DEFAULT_VENUE = 'projectx';
const DEFAULT_INSTRUMENT = 'ES';

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

export interface WorkspaceSurfaceProps {
  readonly destination: Destination;
}

/**
 * The chart-central workspace (gh#725, R-10, ADR-0004): the candlestick chart is the primary surface, with the
 * suggestion panel (gh#654) beside it. This owns the shell's surface contract (the single `data-surface`); the chart
 * and the panel are plain regions within it. The order ticket, the live blotter and the chart overlays are their own
 * cards (gh#655 / gh#656 / gh#727) and join this layout as they land.
 */
export function WorkspaceSurface({ destination }: WorkspaceSurfaceProps): React.JSX.Element {
  const [instrument, setInstrument] = useState(DEFAULT_INSTRUMENT);
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
  // Optional rather than required: this surface can render before the provider resolves, and "no account yet" is
  // NOT "no positions" -- the blotter is withheld rather than mounted against a guess (gh#656).
  const accounts = useOptionalAccounts();
  const activeAccountId = accounts?.status === 'ready' ? accounts.activeAccount.id : null;

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
    <Box
      data-testid="surface"
      data-surface={destination.id}
      sx={{ height: '100%', display: 'flex', flexDirection: { xs: 'column', md: 'row' } }}
    >
      <Box
        sx={{
          flex: { md: 2 },
          minWidth: 0,
          minHeight: { xs: 320, md: 0 },
          display: 'flex',
          flexDirection: 'column',
        }}
      >
        <ChartControls
          instrument={instrument}
          resolution={resolution}
          enabledIndicators={enabledIndicators}
          showLevels={showLevels}
          onInstrument={setInstrument}
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

      <Box
        sx={{
          flex: { md: 1 },
          minWidth: { md: 320 },
          maxWidth: { md: 440 },
          borderColor: 'divider',
          borderStyle: 'solid',
          borderWidth: { xs: '1px 0 0 0', md: '0 0 0 1px' },
        }}
      >
        <SuggestionsPanel />
        {/* The live blotter (gh#656) — venue truth for positions and resting orders, beside the decision
            surface rather than a separate screen: managing what is live is part of the same loop as deciding
            what to take. Withheld entirely until an account resolves, because an empty blotter would assert
            something about venue truth that has not been read. */}
        {activeAccountId !== null ? (
          <Box
            sx={{ borderColor: 'divider', borderStyle: 'solid', borderWidth: '1px 0 0 0', p: 1.5 }}
          >
            <Blotter accountId={activeAccountId} />
          </Box>
        ) : null}
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
