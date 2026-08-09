import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import { type DailyHeadroom, getHeadroom, getRiskProfile, type RiskProfile } from '../api/risk';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
import { HeadroomPanel } from './HeadroomPanel';
import { RiskProfileSummary } from './RiskProfileSummary';

/**
 * The risk section of the settings surface for one account (R-5): its declared profile and today's headroom.
 *
 * The profile is the primary read; **headroom is supplementary** and never fails the surface — a headroom read that
 * errors leaves the declared profile fully visible rather than blanking the screen, because the profile is the
 * thing the operator came to see and headroom is the glance on top of it.
 *
 * The account is passed in, and the parent keys this component on the account id, so switching accounts remounts
 * with a fresh load rather than letting a slow response for the account just left resolve into the new one's view.
 */
export interface RiskSettingsProps {
  readonly accountId: string;
}

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | {
      readonly kind: 'loaded';
      readonly profile: RiskProfile | null;
      readonly headroom: DailyHeadroom | null;
    };

export function RiskSettings({ accountId }: RiskSettingsProps) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    void Promise.all([getRiskProfile(accountId), getHeadroom(accountId)]).then(
      ([profile, headroom]) => {
        if (!mounted.current) {
          return;
        }
        if (!profile.ok) {
          setState({
            kind: 'error',
            message: profile.kind === 'refused' ? profile.reason : profile.error,
          });
          return;
        }
        // Headroom is supplementary: a failed headroom read shows the profile without it, never an error screen.
        setState({
          kind: 'loaded',
          profile: profile.data,
          headroom: headroom.ok ? headroom.data : null,
        });
      },
    );
  }, [accountId]);

  // Mount loads; the initial state is already `loading`, so nothing is set synchronously in the effect.
  useEffect(() => {
    load();
  }, [load]);

  const reload = useCallback(() => {
    setState({ kind: 'loading' });
    load();
  }, [load]);

  if (state.kind === 'loading') {
    return <LoadingState label="Loading the risk profile" />;
  }

  if (state.kind === 'error') {
    return (
      <EmptyState
        title="Could not load the risk profile"
        description={state.message}
        action={
          <Button variant="outlined" size="small" onClick={reload}>
            Try again
          </Button>
        }
        tag="R-5"
      />
    );
  }

  if (state.profile === null) {
    return (
      <EmptyState
        title="No risk profile declared"
        description="This account has no declared risk profile yet, so the gate has nothing to size or refuse against. Declare one to set the daily governor, loss floor and per-trade sizing."
        tag="R-5"
      />
    );
  }

  return (
    <Stack spacing={2}>
      <Alert severity="info" icon={false}>
        These values <strong>declare</strong> what the risk gate enforces on the server. The gate is
        what stops a trade — changing a value here changes what it sizes and refuses against, not
        whether it fires.
      </Alert>
      <Box
        sx={{
          display: 'grid',
          gap: 2,
          gridTemplateColumns: { xs: '1fr', md: 'minmax(0, 2fr) minmax(0, 1fr)' },
          alignItems: 'start',
        }}
      >
        <RiskProfileSummary profile={state.profile} />
        {state.headroom === null ? (
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            Today&apos;s headroom is unavailable right now.
          </Typography>
        ) : (
          <HeadroomPanel headroom={state.headroom} />
        )}
      </Box>
    </Stack>
  );
}
