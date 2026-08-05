import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { useState, type FormEvent } from 'react';
import { Navigate, useNavigate, useSearchParams } from 'react-router';

import { AuthLayout } from './AuthLayout';
import { useAuth } from './AuthProvider';

/**
 * Invitation redemption against `POST /auth/accept-invite` (gh#652). The invitation model is deliberately dormant
 * — ADR-0015 / ADR-0017 are one-deployment-one-operator, and the sanctioned future is read-only mentee observers
 * — so this is the surface the endpoint already implies and **nothing more**: no role selection, no management.
 *
 * Unlike sign-in, a failure here **can** be named: the reason (`invalid, already used, or expired`) is about the
 * invitation, not about whether some account exists, so surfacing it helps rather than leaks.
 */
export function AcceptInvitePage() {
  const { session, acceptInvite } = useAuth();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  // The invite link may carry the token; the field stays editable so a pasted invitation works too.
  const [token, setToken] = useState(() => searchParams.get('token') ?? '');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  if (session.status === 'authenticated') {
    return <Navigate to="/" replace />;
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setSubmitting(true);
    const result = await acceptInvite({ token, password, displayName });
    if (result.ok) {
      navigate('/', { replace: true });
      return;
    }
    setSubmitting(false);
    setError(
      result.kind === 'refused'
        ? result.reason
        : 'The invitation could not be redeemed. Check your connection and try again.',
    );
  }

  return (
    <AuthLayout title="Accept your invitation" subtitle="Trading Co-Pilot">
      <Box component="form" onSubmit={onSubmit} noValidate>
        <Stack spacing={2}>
          {error ? (
            <Alert severity="error" role="alert">
              {error}
            </Alert>
          ) : null}
          <TextField
            label="Invitation token"
            value={token}
            onChange={(event) => setToken(event.target.value)}
            autoComplete="off"
            required
            fullWidth
          />
          <TextField
            label="Display name"
            value={displayName}
            onChange={(event) => setDisplayName(event.target.value)}
            autoComplete="name"
            required
            fullWidth
          />
          <TextField
            label="Choose a password"
            type="password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            autoComplete="new-password"
            required
            fullWidth
          />
          <Button type="submit" variant="contained" size="large" disabled={submitting} fullWidth>
            {submitting ? 'Setting up…' : 'Accept invitation'}
          </Button>
        </Stack>
      </Box>
    </AuthLayout>
  );
}
