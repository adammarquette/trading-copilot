import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router';

import type { ApiResult } from '../api/client';
import { AuthLayout } from './AuthLayout';
import { useAuth } from './AuthProvider';
import { redirectTargetOf } from './redirect';

/**
 * The first surface an operator ever sees (R-18): sign in against `POST /auth/login`. On success the session is
 * established and they land where they were headed. On failure the message never reveals **whether the account
 * exists** — a wrong password and an unknown email read identically — because a login form is an account-
 * enumeration oracle otherwise.
 */
export function SignInPage() {
  const { session, signIn } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  // Already signed in? Never render a credential field over a live session — go where they were headed.
  if (session.status === 'authenticated') {
    return <Navigate to={redirectTargetOf(location)} replace />;
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setSubmitting(true);
    let result: ApiResult<void>;
    try {
      result = await signIn({ email, password });
    } catch {
      // The belt. gh#954 removed the client's own throw on an empty 2xx, but this await was still unguarded, so
      // ANY other throw on the path reproduced the same failure: the handler unwound, `setSubmitting(false)`
      // never ran, and the operator was left on a disabled button with no message and no way forward. The
      // realistic remaining cause is `storeToken` — `localStorage.setItem` throws on a quota or in private mode.
      // A surface must not be strandable by a throw it did not anticipate (gh#963).
      setSubmitting(false);
      setError('Sign-in could not be completed. Check your connection and try again.');
      return;
    }
    if (result.ok) {
      navigate(redirectTargetOf(location), { replace: true });
      return;
    }
    setSubmitting(false);
    setError(
      result.kind === 'failed' && result.status === 401
        ? 'The email or password is incorrect.'
        : 'Sign-in could not be completed. Check your connection and try again.',
    );
  }

  return (
    <AuthLayout title="Sign in" subtitle="Trading Co-Pilot">
      <Box component="form" onSubmit={onSubmit} noValidate>
        <Stack spacing={2}>
          {error ? (
            <Alert severity="error" role="alert">
              {error}
            </Alert>
          ) : null}
          <TextField
            label="Email"
            type="email"
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            autoComplete="username"
            autoFocus
            required
            fullWidth
          />
          <TextField
            label="Password"
            type="password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            autoComplete="current-password"
            required
            fullWidth
          />
          <Button type="submit" variant="contained" size="large" disabled={submitting} fullWidth>
            {submitting ? 'Signing in…' : 'Sign in'}
          </Button>
        </Stack>
      </Box>
    </AuthLayout>
  );
}
