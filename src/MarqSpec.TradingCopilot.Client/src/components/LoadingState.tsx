import Box from '@mui/material/Box';
import CircularProgress from '@mui/material/CircularProgress';
import Typography from '@mui/material/Typography';

/**
 * The one loading affordance every surface uses.
 *
 * A convention rather than a component library: if each surface invents its own spinner, "is this
 * loading or is it broken?" becomes a per-surface judgement call. It is a live region so the answer
 * reaches an assistive-technology user too, and it always carries a label -- a bare spinner tells the
 * operator that something is happening but not what, which in a cockpit is close to useless.
 */
export interface LoadingStateProps {
  /** What is being waited on, e.g. "Loading positions". */
  readonly label: string;
  /** Fill the parent instead of sitting inline. Use inside a pane; leave off inside a row. */
  readonly fullHeight?: boolean;
}

export function LoadingState({ label, fullHeight = true }: LoadingStateProps) {
  return (
    <Box
      role="status"
      aria-live="polite"
      data-testid="loading-state"
      sx={{
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 1.5,
        p: 4,
        ...(fullHeight ? { minHeight: '100%' } : null),
      }}
    >
      <CircularProgress size={28} aria-hidden />
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {label}
      </Typography>
    </Box>
  );
}
