import Typography from '@mui/material/Typography';

export interface BehindMarkerProps {
  /** Defaults to `behind-marker`; pass a surface's own established selector (e.g. `suggestions-stale`). */
  readonly testId?: string;
}

/**
 * The shared, subtle, non-destructive "this read is behind" marker (gh#1109, generalising gh#874). Paired with
 * {@link ../components/useBehindIndicator.useBehindIndicator | useBehindIndicator} — a caller renders this only
 * while `behind` is true, and never on its own loading / error states, which own their own screens and are never
 * shown alongside it. It is a `status` live region, not an `alert`: the surface it decorates is still usable and
 * nothing here should read as urgent or destructive, only as a hint to double-check before acting on it.
 */
export function BehindMarker({ testId = 'behind-marker' }: BehindMarkerProps) {
  return (
    <Typography role="status" data-testid={testId} variant="caption" sx={{ color: 'warning.main' }}>
      The last refresh did not go through — what is shown may be out of date.
    </Typography>
  );
}
