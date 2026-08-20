import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { useCallback, useEffect, useRef, useState } from 'react';

import {
  IndicatorComparison,
  type NewTrigger,
  type Trigger,
  TriggerRoute,
  createTrigger,
} from '../api/triggers';

interface TriggerFormProps {
  /** Called with the server's created row — unconfirmed, because creating is not arming. */
  readonly onCreated: (trigger: Trigger) => void;
}

/**
 * Authoring a standing trigger (gh#1003, increment 2 of gh#991).
 *
 * **Creating is not arming.** The server defaults a new trigger to `Unconfirmed`, which is inert regardless of
 * `enabled` (gh#470), so this form ends by telling the operator a second deliberate act is still required. A form
 * that fell silent on success would leave them believing they had armed something.
 *
 * One conditional matters enough to enforce here rather than let the server teach it: an **agent-review** route
 * needs an account and a positive size. The server refuses either omission by name, and that refusal is rendered
 * verbatim when it happens — but discovering a required field by submitting is a poor way to learn a form.
 */
const INDICATORS = ['rsi', 'atr'] as const;

export function TriggerForm({ onCreated }: TriggerFormProps) {
  const [symbol, setSymbol] = useState('');
  const [indicator, setIndicator] = useState<string>(INDICATORS[0]);
  const [period, setPeriod] = useState('');
  const [resolution, setResolution] = useState('');
  const [comparison, setComparison] = useState(String(IndicatorComparison.Above));
  const [threshold, setThreshold] = useState('');
  const [hysteresis, setHysteresis] = useState('');
  const [route, setRoute] = useState(String(TriggerRoute.Mechanical));
  const [accountId, setAccountId] = useState('');
  const [size, setSize] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const isAgentReview = route === String(TriggerRoute.AgentReview);

  const onSubmit = useCallback(() => {
    setError(null);
    setNotice(null);

    // The one conditional the server owns and the form should not make you discover by submitting.
    if (isAgentReview && (accountId.trim().length === 0 || Number(size) <= 0)) {
      setError('An agent-review trigger needs an account to issue against, and a positive size.');
      return;
    }

    const body: NewTrigger = {
      symbol: symbol.trim(),
      indicator,
      period: Number(period),
      resolutionMinutes: Number(resolution),
      comparison: Number(comparison),
      threshold: Number(threshold),
      route: Number(route),
      hysteresis: hysteresis.trim().length === 0 ? null : Number(hysteresis),
      accountId: isAgentReview ? accountId.trim() : null,
      size: isAgentReview ? Number(size) : null,
    };

    setSubmitting(true);
    void createTrigger(body)
      .then((result) => {
        if (!mounted.current) {
          return;
        }
        setSubmitting(false);
        if (!result.ok) {
          // The server's own words, and the form keeps what was typed — re-typing a form because the server had
          // an opinion is a punishment for the server's opinion.
          setError(result.kind === 'refused' ? result.reason : result.error);
          return;
        }
        // Creating is not arming: say so rather than falling silent on success.
        setNotice('Created. It will not fire until you confirm it.');
        setSymbol('');
        setPeriod('');
        setResolution('');
        setThreshold('');
        setHysteresis('');
        setAccountId('');
        setSize('');
        onCreated(result.data);
      })
      .catch(() => {
        // A surface must not be strandable by a throw it did not anticipate (gh#973).
        if (mounted.current) {
          setSubmitting(false);
          setError('The trigger could not be created.');
        }
      });
  }, [
    accountId,
    comparison,
    hysteresis,
    indicator,
    isAgentReview,
    onCreated,
    period,
    resolution,
    route,
    size,
    symbol,
    threshold,
  ]);

  return (
    <Box
      data-testid="trigger-form"
      sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 2 }}
    >
      <Stack spacing={2}>
        {error ? (
          <Alert severity="error" role="alert">
            {error}
          </Alert>
        ) : null}
        {notice ? (
          <Alert severity="info" role="status">
            {notice}
          </Alert>
        ) : null}

        <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
          <TextField
            label="Instrument"
            size="small"
            value={symbol}
            onChange={(e) => setSymbol(e.target.value)}
          />
          <TextField
            select
            label="Indicator"
            size="small"
            value={indicator}
            onChange={(e) => setIndicator(e.target.value)}
            sx={{ minWidth: 120 }}
          >
            {INDICATORS.map((name) => (
              <MenuItem key={name} value={name}>
                {name}
              </MenuItem>
            ))}
          </TextField>
          <TextField
            label="Period"
            size="small"
            value={period}
            onChange={(e) => setPeriod(e.target.value)}
          />
          <TextField
            label="Bar size (minutes)"
            size="small"
            value={resolution}
            onChange={(e) => setResolution(e.target.value)}
          />
        </Stack>

        <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
          <TextField
            select
            label="Comparison"
            size="small"
            value={comparison}
            onChange={(e) => setComparison(e.target.value)}
            sx={{ minWidth: 120 }}
          >
            <MenuItem value={String(IndicatorComparison.Above)}>above</MenuItem>
            <MenuItem value={String(IndicatorComparison.Below)}>below</MenuItem>
          </TextField>
          <TextField
            label="Threshold"
            size="small"
            value={threshold}
            onChange={(e) => setThreshold(e.target.value)}
          />
          <TextField
            label="Re-arm band (optional)"
            size="small"
            value={hysteresis}
            onChange={(e) => setHysteresis(e.target.value)}
          />
          <TextField
            select
            label="Route"
            size="small"
            value={route}
            onChange={(e) => setRoute(e.target.value)}
            sx={{ minWidth: 160 }}
          >
            <MenuItem value={String(TriggerRoute.Mechanical)}>mechanical alert</MenuItem>
            <MenuItem value={String(TriggerRoute.AgentReview)}>agent review</MenuItem>
          </TextField>
        </Stack>

        {isAgentReview ? (
          <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
            <TextField
              label="Account"
              size="small"
              required
              value={accountId}
              onChange={(e) => setAccountId(e.target.value)}
            />
            <TextField
              label="Size"
              size="small"
              required
              value={size}
              onChange={(e) => setSize(e.target.value)}
            />
          </Stack>
        ) : null}

        <Box>
          <Button variant="contained" disabled={submitting} onClick={onSubmit}>
            Create trigger
          </Button>
        </Box>
      </Stack>
    </Box>
  );
}
