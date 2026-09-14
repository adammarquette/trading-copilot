import StarIcon from '@mui/icons-material/Star';
import StarBorderIcon from '@mui/icons-material/StarBorder';
import VolumeOffIcon from '@mui/icons-material/VolumeOff';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import IconButton from '@mui/material/IconButton';
import Link from '@mui/material/Link';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import {
  clearNewsFeedback,
  getNewsFeed,
  type NewsFeedItem,
  setNewsFeedback,
  SoftSignalKind,
} from '../api/news';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';

/**
 * The personalized news feed (gh#742/gh#27, R-2/R-6, ADR-0014): recent news re-ranked by the operator's own decayed
 * salience profile, each item carrying the plain-language reason it was weighted (never a hidden score) and a
 * star / mute control.
 *
 * **Feedback is a soft salience weight only.** A star raises the salience of similar future items; a mute lowers it
 * (never hiding it — nothing is filtered out). It can move neither a risk limit, a size, nor a gate decision
 * (enforcement lives below the model), so the control is a quiet reorder cue, not a decision surface.
 *
 * The rating **round-trips**: clicking the active rating again clears it, and the server upsert means a star over a
 * mute simply replaces. The item's own state updates optimistically on success rather than re-fetching, so a rating
 * does not reshuffle the list under the operator's cursor — the personalized re-rank applies on the next load.
 */
type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly items: readonly NewsFeedItem[] };

export function NewsFeed() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [actionError, setActionError] = useState<string | null>(null);
  // Per-item in-flight guard, keyed by dedup key so one item's rating never disables another's. Two halves: the
  // `rating` STATE disables the row's buttons (the visible half), and the `inFlight` REF excludes a second click in
  // the SAME tick (the correct half) -- a state Set is not applied until the next render, so two clicks dispatched
  // together would both read it empty and both fire (the OrderTicket ref lesson).
  const [rating, setRating] = useState<ReadonlySet<string>>(() => new Set());
  const inFlight = useRef<Set<string>>(new Set());
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    void getNewsFeed().then((result) => {
      if (!mounted.current) {
        return;
      }
      setState(
        result.ok
          ? { kind: 'loaded', items: result.data }
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

  const rate = useCallback((item: NewsFeedItem, kind: SoftSignalKind) => {
    if (inFlight.current.has(item.dedupKey)) {
      return; // never a second rating of the same item before the first resolves (synchronous exclusion)
    }
    // Clicking the ACTIVE rating clears it; any other click sets the new kind (the server upsert replaces).
    const target: SoftSignalKind | null = item.feedback === kind ? null : kind;

    inFlight.current.add(item.dedupKey);
    setRating((current) => new Set(current).add(item.dedupKey));
    const send =
      target === null ? clearNewsFeedback(item.dedupKey) : setNewsFeedback(item.dedupKey, target);
    void send.then((result) => {
      inFlight.current.delete(item.dedupKey);
      if (!mounted.current) {
        return;
      }
      setRating((current) => {
        const next = new Set(current);
        next.delete(item.dedupKey);
        return next;
      });
      if (!result.ok) {
        setActionError(result.kind === 'refused' ? result.reason : result.error);
        return;
      }
      setActionError(null);
      // Reflect the rating on the item in place -- do not re-fetch, which would reshuffle the list mid-glance.
      setState((current) =>
        current.kind === 'loaded'
          ? {
              kind: 'loaded',
              items: current.items.map((candidate) =>
                candidate.dedupKey === item.dedupKey
                  ? { ...candidate, feedback: target }
                  : candidate,
              ),
            }
          : current,
      );
    });
  }, []);

  return (
    <Box data-testid="news-feed" sx={{ mt: 3 }}>
      <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>
        Feed
      </Typography>
      <Typography variant="body2" sx={{ color: 'text.secondary', mb: 1.5 }}>
        Ranked for you by what you star and mute. Feedback only reorders the feed — it never moves a
        risk limit or a trade (R-6).
      </Typography>

      {actionError !== null ? (
        <Alert severity="warning" sx={{ mb: 2 }} onClose={() => setActionError(null)}>
          {actionError}
        </Alert>
      ) : null}

      {state.kind === 'loading' ? <LoadingState label="Loading the feed" /> : null}

      {state.kind === 'error' ? (
        <EmptyState
          title="Could not load the feed"
          description={state.message}
          action={
            <Button variant="outlined" size="small" onClick={reload}>
              Try again
            </Button>
          }
          tag="R-2"
        />
      ) : null}

      {state.kind === 'loaded' && state.items.length === 0 ? (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          No news yet — items appear here as they are ingested, ranked by relevance to what you
          trade.
        </Typography>
      ) : null}

      {state.kind === 'loaded' && state.items.length > 0 ? (
        <Stack spacing={1}>
          {state.items.map((item) => (
            <Paper
              key={item.dedupKey}
              variant="outlined"
              data-testid="news-item"
              data-dedup-key={item.dedupKey}
              data-feedback={feedbackLabel(item.feedback)}
              sx={{ px: 2, py: 1.5 }}
            >
              <Stack direction="row" spacing={1} alignItems="flex-start">
                <Box sx={{ flex: 1, minWidth: 0 }}>
                  <Link
                    href={item.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    variant="body2"
                    sx={{ fontWeight: 600 }}
                  >
                    {item.title}
                  </Link>
                  <Stack
                    direction="row"
                    spacing={0.5}
                    useFlexGap
                    flexWrap="wrap"
                    sx={{ mt: 0.5, alignItems: 'center' }}
                  >
                    {item.sources.map((source) => (
                      <Chip key={source} size="small" variant="outlined" label={source} />
                    ))}
                    {item.instruments.map((instrument) => (
                      <Chip
                        key={instrument}
                        size="small"
                        color="primary"
                        variant="outlined"
                        label={instrument}
                      />
                    ))}
                    {item.topics.map((topic) => (
                      <Chip key={topic} size="small" variant="outlined" label={topic} />
                    ))}
                  </Stack>
                  {/* The weighting is explained, never a hidden score (ADR-0014). */}
                  <Typography
                    variant="caption"
                    sx={{ display: 'block', mt: 0.5, color: 'text.secondary' }}
                  >
                    {item.whyWeighted}
                  </Typography>
                </Box>

                <Stack direction="row" spacing={0.5}>
                  <IconButton
                    size="small"
                    aria-label={
                      item.feedback === SoftSignalKind.Star ? 'Un-star this item' : 'Star this item'
                    }
                    aria-pressed={item.feedback === SoftSignalKind.Star}
                    disabled={rating.has(item.dedupKey)}
                    onClick={() => rate(item, SoftSignalKind.Star)}
                  >
                    {item.feedback === SoftSignalKind.Star ? (
                      <StarIcon fontSize="small" color="warning" />
                    ) : (
                      <StarBorderIcon fontSize="small" />
                    )}
                  </IconButton>
                  <IconButton
                    size="small"
                    aria-label={
                      item.feedback === SoftSignalKind.Mute
                        ? 'Un-mute similar items'
                        : 'Mute similar items'
                    }
                    aria-pressed={item.feedback === SoftSignalKind.Mute}
                    disabled={rating.has(item.dedupKey)}
                    onClick={() => rate(item, SoftSignalKind.Mute)}
                  >
                    <VolumeOffIcon
                      fontSize="small"
                      color={item.feedback === SoftSignalKind.Mute ? 'error' : 'inherit'}
                    />
                  </IconButton>
                </Stack>
              </Stack>
            </Paper>
          ))}
        </Stack>
      ) : null}
    </Box>
  );
}

/** The rendered `data-feedback` marker, so a test reads the item's rating without reaching into the icons. */
function feedbackLabel(feedback: SoftSignalKind | null): string {
  switch (feedback) {
    case SoftSignalKind.Star:
      return 'star';
    case SoftSignalKind.Mute:
      return 'mute';
    default:
      return 'none';
  }
}
