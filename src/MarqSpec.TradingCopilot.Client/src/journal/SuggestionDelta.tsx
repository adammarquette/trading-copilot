import Box from '@mui/material/Box';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';

import {
  type Suggestion,
  SuggestionDeviation,
  SuggestionDispositionKind,
} from '../api/suggestions';
import { formatRatio } from '../settings/format';
import { formatPrice } from './format';

/**
 * The taken-vs-suggested delta on one trade (gh#659, R-9) — what the co-pilot proposed beside what the
 * operator actually sent, field by field.
 *
 * This is the R-9 signal the PRD calls "the taken-vs-suggested delta": it is what exposes whether operator
 * discretion adds or subtracts edge. Two rules keep it honest:
 *
 * - **The deviation marks come from the server's bitmask, never from comparing the two prices here.** The mask
 *   was computed at take time against an exact-decimal snapshot (gh#549) and is what R-9 aggregates; a second,
 *   client-side comparison would drift from it the moment either side rounds differently, and the surface
 *   would then disagree with the report.
 * - **No disposition is not a decision.** A suggestion that expired carries no disposition row at all
 *   (gh#539) — R-9 reads that as the clock acting, never as the operator acting, and so does this. The
 *   suggested column still renders, because what was proposed is a fact regardless.
 */
export interface SuggestionDeltaProps {
  readonly suggestion: Suggestion;
}

const DISPOSITION_LABELS: Record<number, string> = {
  [SuggestionDispositionKind.Taken]: 'Taken as suggested',
  [SuggestionDispositionKind.Modified]: 'Modified',
  [SuggestionDispositionKind.Passed]: 'Passed',
};

function Row({
  id,
  label,
  suggested,
  taken,
  deviated,
}: {
  readonly id: string;
  readonly label: string;
  readonly suggested: string;
  readonly taken: string;
  readonly deviated: boolean;
}) {
  return (
    <Box
      data-testid={`delta-${id}`}
      data-deviated={String(deviated)}
      sx={{
        display: 'grid',
        gridTemplateColumns: 'minmax(56px, 1fr) minmax(64px, 1fr) minmax(64px, 1fr)',
        gap: 1,
        alignItems: 'baseline',
      }}
    >
      <Typography variant="caption" sx={{ color: 'text.secondary' }}>
        {label}
      </Typography>
      <Typography variant="caption" sx={{ fontVariantNumeric: 'tabular-nums' }}>
        {suggested}
      </Typography>
      <Typography
        variant="caption"
        sx={{
          fontVariantNumeric: 'tabular-nums',
          fontWeight: deviated ? 700 : 400,
          color: deviated ? 'warning.main' : 'text.primary',
        }}
      >
        {taken}
        {deviated ? ' *' : ''}
      </Typography>
    </Box>
  );
}

export function SuggestionDelta({ suggestion }: SuggestionDeltaProps) {
  const disposition = suggestion.disposition;
  const deviations = disposition?.deviations ?? SuggestionDeviation.None;
  const deviated = (flag: SuggestionDeviation) => (deviations & flag) !== 0;

  return (
    <Stack spacing={1} data-testid="suggestion-delta">
      <Stack direction="row" spacing={1} alignItems="center" sx={{ flexWrap: 'wrap', gap: 0.5 }}>
        <Chip
          size="small"
          variant="outlined"
          label={
            disposition === null
              ? 'No disposition recorded'
              : (DISPOSITION_LABELS[disposition.kind] ?? 'No disposition recorded')
          }
        />
        {suggestion.rewardRiskRatio === null ? null : (
          <Chip
            size="small"
            variant="outlined"
            label={formatRatio(suggestion.rewardRiskRatio)}
            title="The suggestion's own reward:risk"
          />
        )}
      </Stack>

      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: 'minmax(56px, 1fr) minmax(64px, 1fr) minmax(64px, 1fr)',
          gap: 1,
        }}
      >
        <Typography variant="caption" sx={{ color: 'text.disabled' }} />
        <Typography variant="caption" sx={{ color: 'text.disabled', fontWeight: 700 }}>
          Suggested
        </Typography>
        <Typography variant="caption" sx={{ color: 'text.disabled', fontWeight: 700 }}>
          Taken
        </Typography>
      </Box>

      <Row
        id="entry"
        label="Entry"
        suggested={formatPrice(suggestion.entryPrice)}
        taken={formatPrice(disposition?.takenEntryPrice ?? null)}
        deviated={deviated(SuggestionDeviation.Entry)}
      />
      <Row
        id="stop"
        label="Stop"
        suggested={formatPrice(suggestion.stopPrice)}
        taken={formatPrice(disposition?.takenStopPrice ?? null)}
        deviated={deviated(SuggestionDeviation.Stop)}
      />
      <Row
        id="target"
        label="Target"
        suggested={formatPrice(suggestion.targetPrice)}
        taken={formatPrice(disposition?.takenTargetPrice ?? null)}
        deviated={deviated(SuggestionDeviation.Target)}
      />
      <Row
        id="size"
        label="Size"
        suggested={String(suggestion.size)}
        taken={
          disposition?.takenSize === null || disposition === null
            ? '—'
            : String(disposition.takenSize)
        }
        deviated={deviated(SuggestionDeviation.Size)}
      />

      {disposition?.note === null || disposition === null ? null : (
        <Typography variant="caption" sx={{ color: 'text.secondary' }}>
          {disposition.note}
        </Typography>
      )}
    </Stack>
  );
}
