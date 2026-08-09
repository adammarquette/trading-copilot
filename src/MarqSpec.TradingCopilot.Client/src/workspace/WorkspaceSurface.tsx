import Box from '@mui/material/Box';
import TextField from '@mui/material/TextField';
import { useState } from 'react';

import { MarketChart } from '../chart/MarketChart';
import type { Destination } from '../navigation/destinations';
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
          onInstrument={setInstrument}
          onResolution={setResolution}
        />
        <Box sx={{ flex: 1, minHeight: 0 }}>
          {/* Key on the series identity so a change remounts the chart (fresh loading + fetch, no in-effect setState). */}
          <MarketChart
            key={`${DEFAULT_VENUE}:${instrument}:${resolution}`}
            venue={DEFAULT_VENUE}
            instrument={instrument}
            resolution={resolution}
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
      </Box>
    </Box>
  );
}

interface ChartControlsProps {
  readonly instrument: string;
  readonly resolution: number;
  readonly onInstrument: (instrument: string) => void;
  readonly onResolution: (resolution: number) => void;
}

/**
 * The instrument + resolution picker. The instrument **commits on submit** (Enter) so the chart does not refetch on
 * every keystroke; the resolution commits on select. A native select keeps it operable and testable without a menu.
 */
function ChartControls({
  instrument,
  resolution,
  onInstrument,
  onResolution,
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
      sx={{ display: 'flex', gap: 1, alignItems: 'center', px: 2, py: 1 }}
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
    </Box>
  );
}
