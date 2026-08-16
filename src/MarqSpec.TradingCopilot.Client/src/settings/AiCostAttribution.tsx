import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import { type AiAttribution, getAiAttribution } from '../api/ai';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';

/**
 * The AI cost-attribution section of settings (gh#767): what each *decision* cost — per suggestion, and per taken
 * trade against its realized outcome — so "is a suggestion worth what it cost?" is answerable, not just "am I near the
 * cap?" (the aggregate `AiSpendSettings` beside it, gh#741). A pure read from the durable AIUsage ledger (ADR-0002),
 * owner-scoped (R-20). Display only — it declares and enforces nothing.
 */
type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly attribution: AiAttribution };

/** Formats a dollar amount for display only. Sub-cent AI costs need more than 2 dp so a real cost never reads as $0.00. */
function usd(value: number): string {
  return `$${value.toFixed(value !== 0 && Math.abs(value) < 0.01 ? 4 : 2)}`;
}

/** Formats a signed realized P&L, or an em-dash when none is recorded. */
function pnl(value: number | null): string {
  if (value === null) {
    return '—';
  }
  const sign = value > 0 ? '+' : '';
  return `${sign}$${value.toFixed(2)}`;
}

export function AiCostAttribution() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    void getAiAttribution().then((result) => {
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
      setState({ kind: 'loaded', attribution: result.data });
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
    return <LoadingState label="Loading AI cost attribution" />;
  }

  if (state.kind === 'error') {
    return (
      <EmptyState
        title="Could not load AI cost attribution"
        description={state.message}
        action={
          <Button variant="outlined" size="small" onClick={reload}>
            Try again
          </Button>
        }
        tag="gh#767"
      />
    );
  }

  const { attribution } = state;
  const nothing =
    attribution.suggestions.length === 0 &&
    attribution.takenTrades.length === 0 &&
    attribution.unattributedUsd === 0;

  if (nothing) {
    return (
      <Typography
        variant="body2"
        sx={{ color: 'text.secondary' }}
        data-testid="ai-attribution-empty"
      >
        No AI-costed suggestions in this period yet.
      </Typography>
    );
  }

  return (
    <Stack spacing={2} data-testid="ai-attribution">
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        What each decision cost, {attribution.from.slice(0, 10)} to {attribution.to.slice(0, 10)}.
      </Typography>

      {attribution.suggestions.length > 0 ? (
        <Box data-testid="ai-attribution-suggestions">
          <Typography variant="subtitle2" gutterBottom>
            Cost per suggestion
          </Typography>
          <Stack spacing={0.75}>
            {attribution.suggestions.map((suggestion) => (
              <Stack
                key={suggestion.suggestionId}
                direction="row"
                justifyContent="space-between"
                alignItems="center"
              >
                <Stack direction="row" spacing={1} alignItems="center">
                  <Typography variant="body2">
                    {suggestion.instrument} {suggestion.side}
                  </Typography>
                  {suggestion.escalated ? (
                    <Chip label="escalated" size="small" variant="outlined" />
                  ) : null}
                </Stack>
                <Typography variant="body2" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                  {usd(suggestion.costUsd)}
                </Typography>
              </Stack>
            ))}
          </Stack>
        </Box>
      ) : null}

      {attribution.takenTrades.length > 0 ? (
        <Box data-testid="ai-attribution-trades">
          <Typography variant="subtitle2" gutterBottom>
            Cost per taken trade
          </Typography>
          <Stack spacing={0.75}>
            {attribution.takenTrades.map((trade) => (
              <Stack
                key={trade.tradeId}
                direction="row"
                justifyContent="space-between"
                alignItems="center"
              >
                <Typography variant="body2">{trade.instrument}</Typography>
                <Typography
                  variant="body2"
                  component="div"
                  sx={{ fontVariantNumeric: 'tabular-nums' }}
                >
                  <Box component="span" sx={{ color: 'text.secondary' }}>
                    {usd(trade.suggestionCostUsd)} cost
                  </Box>{' '}
                  <Box
                    component="span"
                    sx={{
                      color:
                        trade.realizedPnL !== null && trade.realizedPnL < 0
                          ? 'error.main'
                          : 'success.main',
                    }}
                  >
                    {pnl(trade.realizedPnL)}
                  </Box>
                </Typography>
              </Stack>
            ))}
          </Stack>
        </Box>
      ) : null}

      {attribution.unattributedUsd > 0 ? (
        <Typography
          variant="caption"
          component="p"
          sx={{ color: 'text.secondary' }}
          data-testid="ai-attribution-unattributed"
        >
          {usd(attribution.unattributedUsd)} on reviews that produced no suggestion (suppressed or
          throttled) — real spend, not attributable to a decision.
        </Typography>
      ) : null}
    </Stack>
  );
}
