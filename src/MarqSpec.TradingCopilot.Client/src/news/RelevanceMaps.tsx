import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { type FormEvent, useCallback, useEffect, useRef, useState } from 'react';

import { createMap, deleteMap, listMaps, type TickerMap } from '../api/relevance';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';

/**
 * The ticker↔instrument maps (gh#742, R-2): which provider ticker (`SPY`) attaches its news to which traded
 * instrument (`ES`). Deployment-global config, so no account scoping. The pair is the map's key — a second map for
 * the same pair conflicts rather than duplicating — so a create surfaces the server's refusal rather than
 * pretending it worked, and a delete names both halves.
 */
type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly maps: readonly TickerMap[] };

export function RelevanceMaps() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [ticker, setTicker] = useState('');
  const [instrument, setInstrument] = useState('');
  const [actionError, setActionError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    void listMaps().then((result) => {
      if (!mounted.current) {
        return;
      }
      setState(
        result.ok
          ? { kind: 'loaded', maps: result.data }
          : { kind: 'error', message: result.kind === 'refused' ? result.reason : result.error },
      );
    });
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const reload = useCallback(() => {
    setState({ kind: 'loading' });
    load();
  }, [load]);

  const failed = (
    result: { kind: 'refused'; reason: string } | { kind: 'failed'; error: string },
  ) => setActionError(result.kind === 'refused' ? result.reason : result.error);

  const add = useCallback(
    (event: FormEvent) => {
      event.preventDefault();
      const pair = { ticker: ticker.trim(), instrument: instrument.trim() };
      if (pair.ticker === '' || pair.instrument === '') {
        return;
      }
      void createMap(pair).then((result) => {
        if (!mounted.current) {
          return;
        }
        if (result.ok) {
          setTicker('');
          setInstrument('');
          setActionError(null);
          reload();
        } else {
          failed(result);
        }
      });
    },
    [ticker, instrument, reload],
  );

  const remove = useCallback(
    (map: TickerMap) => {
      void deleteMap(map.ticker, map.instrument).then((result) => {
        if (!mounted.current) {
          return;
        }
        if (result.ok) {
          setActionError(null);
          reload();
        } else {
          failed(result);
        }
      });
    },
    [reload],
  );

  return (
    <Box data-testid="relevance-maps">
      <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>
        Ticker → instrument maps
      </Typography>
      <Typography variant="body2" sx={{ color: 'text.secondary', mb: 1.5 }}>
        Attach a provider ticker&apos;s news to the instrument you trade — <code>SPY</code> →{' '}
        <code>ES</code>.
      </Typography>

      <Box component="form" onSubmit={add} sx={{ mb: 2 }}>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} alignItems="flex-start">
          <TextField
            label="Ticker"
            value={ticker}
            onChange={(event) => setTicker(event.target.value)}
            size="small"
          />
          <TextField
            label="Instrument"
            value={instrument}
            onChange={(event) => setInstrument(event.target.value)}
            size="small"
          />
          <Button
            type="submit"
            variant="contained"
            disabled={ticker.trim() === '' || instrument.trim() === ''}
          >
            Add
          </Button>
        </Stack>
      </Box>

      {actionError !== null ? (
        <Alert severity="warning" sx={{ mb: 2 }} onClose={() => setActionError(null)}>
          {actionError}
        </Alert>
      ) : null}

      {state.kind === 'loading' ? <LoadingState label="Loading the maps" /> : null}

      {state.kind === 'error' ? (
        <EmptyState
          title="Could not load the maps"
          description={state.message}
          action={
            <Button variant="outlined" size="small" onClick={reload}>
              Try again
            </Button>
          }
          tag="R-2"
        />
      ) : null}

      {state.kind === 'loaded' && state.maps.length === 0 ? (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          No maps yet — add one above to start attaching a ticker&apos;s news to an instrument.
        </Typography>
      ) : null}

      {state.kind === 'loaded' && state.maps.length > 0 ? (
        <Stack spacing={1}>
          {state.maps.map((map) => (
            <Paper
              key={`${map.ticker}→${map.instrument}`}
              variant="outlined"
              sx={{
                px: 2,
                py: 1,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'space-between',
              }}
            >
              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                {map.ticker} → {map.instrument}
              </Typography>
              <IconButton
                size="small"
                aria-label={`Delete the ${map.ticker} to ${map.instrument} map`}
                onClick={() => remove(map)}
              >
                <DeleteOutlineIcon fontSize="small" />
              </IconButton>
            </Paper>
          ))}
        </Stack>
      ) : null}
    </Box>
  );
}
