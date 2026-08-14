import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import LinearProgress from '@mui/material/LinearProgress';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import { type AiSpend, getAiSpend } from '../api/ai';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';

/**
 * The AI-spend section of the settings surface (gh#741, gh#62): the operator's own AI spend, read from the durable
 * AIUsage ledger — never the export-only Prometheus meter (ADR-0002). "Your keys, your bill" (ADR-0015): at the daily
 * cap the co-pilot stops proposing and says so, and it never blocks manual trading. A pure read — it declares and
 * enforces nothing.
 *
 * The governor's cap is a DAILY budget, so the headline is <b>today's</b> spend against it; the period total is
 * context. Operator-level (R-20), not per-account — so, unlike the risk section, it is not keyed on the active account.
 */
type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly spend: AiSpend };

/** Formats a dollar amount for display only — never a value anything is sized or gated against. */
function usd(value: number): string {
  return `$${value.toFixed(2)}`;
}

export function AiSpendSettings() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    void getAiSpend().then((result) => {
      if (!mounted.current) {
        return;
      }
      if (!result.ok) {
        setState({
          kind: 'error',
          message: result.kind === 'refused' ? result.reason : result.error,
        });
        return;
      }
      setState({ kind: 'loaded', spend: result.data });
    });
  }, []);

  // Mount loads; the initial state is already `loading`, so nothing is set synchronously in the effect.
  useEffect(() => {
    load();
  }, [load]);

  const reload = useCallback(() => {
    setState({ kind: 'loading' });
    load();
  }, [load]);

  if (state.kind === 'loading') {
    return <LoadingState label="Loading AI spend" />;
  }

  if (state.kind === 'error') {
    return (
      <EmptyState
        title="Could not load AI spend"
        description={state.message}
        action={
          <Button variant="outlined" size="small" onClick={reload}>
            Try again
          </Button>
        }
        tag="gh#741"
      />
    );
  }

  const { spend } = state;
  const cap = spend.dailyBudgetUsd;
  const hasCap = cap !== null && cap > 0;
  const overCap = hasCap && spend.todayUsd > cap;
  const capPct = hasCap ? Math.min(100, (spend.todayUsd / cap) * 100) : 0;

  return (
    <Stack spacing={2} data-testid="ai-spend">
      <Alert severity="info" icon={false}>
        Billed to <strong>your own</strong> Anthropic and Cohere keys (ADR-0015). At the daily cap
        the co-pilot <strong>stops proposing</strong> and says so — it never keeps spending quietly,
        and it never blocks you from trading manually.
      </Alert>

      <Box>
        <Stack direction="row" justifyContent="space-between" alignItems="baseline">
          <Typography variant="subtitle2">Today</Typography>
          <Typography variant="body2" component="div" sx={{ fontVariantNumeric: 'tabular-nums' }}>
            <Box component="span" data-testid="ai-spend-today">
              {usd(spend.todayUsd)}
            </Box>{' '}
            <Box component="span" sx={{ color: 'text.secondary' }}>
              {hasCap ? `of ${usd(cap)} daily cap` : '· no daily cap set'}
            </Box>
          </Typography>
        </Stack>
        {hasCap ? (
          <LinearProgress
            variant="determinate"
            value={capPct}
            color={overCap ? 'error' : 'primary'}
            sx={{ mt: 0.5, height: 8, borderRadius: 1 }}
          />
        ) : null}
        {overCap ? (
          <Typography variant="caption" sx={{ color: 'error.main' }}>
            Over today’s cap — the co-pilot has stopped proposing until it resets. Manual trading is
            unaffected.
          </Typography>
        ) : null}
      </Box>

      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {usd(spend.totalUsd)} total from {spend.from.slice(0, 10)} to {spend.to.slice(0, 10)}.
      </Typography>

      {spend.byModel.length === 0 ? (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          No AI spend in this period yet.
        </Typography>
      ) : (
        <Box data-testid="ai-spend-by-model">
          <Typography variant="subtitle2" gutterBottom>
            By model
          </Typography>
          <Stack spacing={1}>
            {spend.byModel.map((slice) => (
              <Box key={slice.model}>
                <Stack direction="row" justifyContent="space-between">
                  <Typography variant="body2">{slice.model}</Typography>
                  <Typography variant="body2" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                    {usd(slice.costUsd)}
                  </Typography>
                </Stack>
                <LinearProgress
                  variant="determinate"
                  value={
                    spend.totalUsd > 0 ? Math.round((slice.costUsd / spend.totalUsd) * 100) : 0
                  }
                  sx={{ height: 6, borderRadius: 1 }}
                />
              </Box>
            ))}
          </Stack>
        </Box>
      )}

      {spend.byDay.length > 0 ? (
        <Box data-testid="ai-spend-by-day">
          <Typography variant="subtitle2" gutterBottom>
            By day
          </Typography>
          <Stack spacing={0.25}>
            {spend.byDay.map((slice) => (
              <Stack key={slice.day} direction="row" justifyContent="space-between">
                <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                  {slice.day}
                </Typography>
                <Typography variant="body2" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                  {usd(slice.costUsd)}
                </Typography>
              </Stack>
            ))}
          </Stack>
        </Box>
      ) : null}
    </Stack>
  );
}
