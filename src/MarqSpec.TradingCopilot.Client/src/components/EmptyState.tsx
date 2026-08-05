import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';

/**
 * The one empty affordance every surface uses -- "there is nothing here" told apart from "this failed"
 * and from "this is still loading".
 *
 * The title is required and the body is optional, in that order, because the failure mode of empty
 * states is a lone illustration that leaves the operator guessing whether a filter is hiding rows, the
 * feed is down, or nothing has happened yet.
 */
export interface EmptyStateProps {
  readonly title: string;
  readonly description?: ReactNode;
  /** A single next step, if there is an obvious one. Not a place for a row of buttons. */
  readonly action?: ReactNode;
  /** Decorative; it is `aria-hidden` because the title already carries the meaning. */
  readonly icon?: ReactNode;
  /** A `R-#` / `gh#N` tag, as the wireframes tag each region. */
  readonly tag?: string;
}

export function EmptyState({ title, description, action, icon, tag }: EmptyStateProps) {
  return (
    <Box
      data-testid="empty-state"
      sx={{
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 1.25,
        minHeight: '100%',
        p: 4,
        textAlign: 'center',
      }}
    >
      {icon ? (
        <Box aria-hidden sx={{ color: 'text.disabled', display: 'flex' }}>
          {icon}
        </Box>
      ) : null}

      <Typography variant="h6" component="h2" sx={{ fontWeight: 600 }}>
        {title}
      </Typography>

      {description ? (
        <Typography variant="body2" sx={{ color: 'text.secondary', maxWidth: '46ch' }}>
          {description}
        </Typography>
      ) : null}

      {tag ? (
        <Typography
          variant="numeric"
          sx={{
            color: 'primary.main',
            bgcolor: 'primary.light',
            px: 1,
            py: 0.25,
            borderRadius: 5,
            fontSize: 10.5,
          }}
        >
          {tag}
        </Typography>
      ) : null}

      {action}
    </Box>
  );
}
