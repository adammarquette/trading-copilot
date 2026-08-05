import Box from '@mui/material/Box';
import Paper from '@mui/material/Paper';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';

/**
 * The frame for the anonymous surfaces — sign-in and accept-invite (gh#652, R-18).
 *
 * Deliberately **not** the {@link AppShell}: these render before there is a session, so they carry no navigation,
 * no safety region and no account state. The wireframe keeps credentials on a surface of their own, and the rule
 * behind it is a real one — a credential field must never share a screen with account state, where a shoulder-
 * surfer or a screenshot leaks both at once. A single centred card, and nothing else.
 */
export interface AuthLayoutProps {
  readonly title: string;
  readonly subtitle?: string;
  readonly children: ReactNode;
}

export function AuthLayout({ title, subtitle, children }: AuthLayoutProps) {
  return (
    <Box
      sx={{
        minHeight: '100dvh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        p: 2,
        bgcolor: 'background.default',
      }}
    >
      <Paper elevation={2} sx={{ width: '100%', maxWidth: 400, p: { xs: 3, sm: 4 } }}>
        {subtitle ? (
          <Typography variant="overline" sx={{ display: 'block', color: 'text.secondary' }}>
            {subtitle}
          </Typography>
        ) : null}
        <Typography variant="h5" component="h1" sx={{ mb: 3 }}>
          {title}
        </Typography>
        {children}
      </Paper>
    </Box>
  );
}
