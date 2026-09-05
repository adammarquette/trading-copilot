import Box from '@mui/material/Box';
import ButtonBase from '@mui/material/ButtonBase';
import Chip from '@mui/material/Chip';
import Collapse from '@mui/material/Collapse';
import Divider from '@mui/material/Divider';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useTheme } from '@mui/material/styles';
import { useCallback, useEffect, useRef, useState } from 'react';

import type { JournalTrade } from '../api/journal';
import { getSuggestion, type Suggestion } from '../api/suggestions';
import { LoadingState } from '../components/LoadingState';
import { toNumber } from '../settings/format';
import { formatPrice, formatSignedUsd } from './format';
import { SuggestionDelta } from './SuggestionDelta';
import { TradeFeedbackPanel } from './TradeFeedbackPanel';

/**
 * One closed trade in the day drill-down (gh#659, R-8) — the round trip at a glance, opening onto the
 * suggestion that produced it (R-9's taken-vs-suggested delta) and the operator's own feedback.
 *
 * **Nothing is read until the trade is opened.** A day is a list, and eagerly loading every trade's
 * suggestion and feedback would turn one drill-down into a burst of reads the operator never asked for. The
 * suggestion is then cached for the life of the card, so toggling the row is free.
 *
 * **The delta is supplementary; the feedback is not.** A suggestion read that fails leaves the feedback panel
 * fully usable — the operator's own note is the thing R-8 promises them, and losing the co-pilot's side of the
 * comparison must not take it down too.
 *
 * **A manual trade says so.** A trade with no originating suggestion renders that plainly rather than an empty
 * suggested column, which would read as a suggestion the co-pilot failed to record.
 */
export interface JournalTradeCardProps {
  readonly trade: JournalTrade;
}

type SuggestionState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'loading' }
  | { readonly kind: 'error' }
  | { readonly kind: 'loaded'; readonly suggestion: Suggestion };

export function JournalTradeCard({ trade }: JournalTradeCardProps) {
  const theme = useTheme();
  const [open, setOpen] = useState(false);
  const [suggestion, setSuggestion] = useState<SuggestionState>({ kind: 'idle' });
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const suggestionId = trade.suggestionId;
  // A ref, not the state itself, because the guard must hold across a re-render *and* the double invocation
  // React makes of a state updater in StrictMode — putting the request inside `setSuggestion` would issue it
  // twice there.
  const requested = useRef(false);

  const toggle = useCallback(() => {
    setOpen((current) => !current);
    // Read once, on the first open. A second open — or a failed first one — never re-issues the request
    // silently behind the operator's back.
    if (suggestionId === null || requested.current) {
      return;
    }
    requested.current = true;
    setSuggestion({ kind: 'loading' });
    void getSuggestion(suggestionId).then((result) => {
      if (!mounted.current) {
        return;
      }
      setSuggestion(result.ok ? { kind: 'loaded', suggestion: result.data } : { kind: 'error' });
    });
  }, [suggestionId]);

  const long = trade.side === 'Buy';
  const realized = toNumber(trade.realizedPnL) ?? 0;

  return (
    <Paper variant="outlined" data-testid="journal-trade">
      <ButtonBase
        onClick={toggle}
        aria-expanded={open}
        sx={{ width: '100%', display: 'block', textAlign: 'left', p: 1.5 }}
      >
        <Stack direction="row" spacing={1} alignItems="center" sx={{ flexWrap: 'wrap', gap: 0.75 }}>
          <Typography
            variant="caption"
            sx={{
              fontWeight: 800,
              color: long ? theme.palette.trading.long : theme.palette.trading.short,
            }}
          >
            {long ? 'LONG' : 'SHORT'}
          </Typography>
          <Typography variant="body2" sx={{ fontWeight: 700 }}>
            {trade.instrument}
          </Typography>
          <Typography
            variant="caption"
            sx={{ color: 'text.secondary', fontVariantNumeric: 'tabular-nums' }}
          >
            {`${String(trade.size)} @ ${formatPrice(trade.entryPrice)} → ${formatPrice(trade.exitPrice)}`}
          </Typography>
          {trade.suggestionId === null ? null : (
            <Chip size="small" variant="outlined" label="from a suggestion" />
          )}
          <Typography
            variant="body2"
            sx={{
              ml: 'auto',
              fontWeight: 700,
              fontVariantNumeric: 'tabular-nums',
              color: realized < 0 ? theme.palette.trading.short : theme.palette.trading.long,
            }}
          >
            {formatSignedUsd(realized)}
          </Typography>
        </Stack>
      </ButtonBase>

      <Collapse in={open} unmountOnExit={false} mountOnEnter>
        <Divider />
        <Box sx={{ p: 1.5 }}>
          <Stack spacing={1.5}>
            {suggestionId === null ? (
              <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                Entered manually — no suggestion behind this trade.
              </Typography>
            ) : suggestion.kind === 'loaded' ? (
              <SuggestionDelta suggestion={suggestion.suggestion} />
            ) : suggestion.kind === 'error' ? (
              <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                The originating suggestion could not be read.
              </Typography>
            ) : (
              <LoadingState label="Loading the suggestion" fullHeight={false} />
            )}

            <Divider />
            <TradeFeedbackPanel tradeId={trade.id} />
          </Stack>
        </Box>
      </Collapse>
    </Paper>
  );
}
