import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useCallback, useEffect, useRef, useState } from 'react';

import {
  addTradeFeedback,
  COMMENT_MAX_LENGTH,
  EMOTIONAL_STATE_MAX_LENGTH,
  getTradeFeedback,
  type TradeFeedbackSummary,
} from '../api/journal';
import { LoadingState } from '../components/LoadingState';

/**
 * The operator's feedback on one closed trade (gh#659, R-8) — what is already recorded, and the form that adds
 * more.
 *
 * R-8 is explicit that this is **optional and asynchronous**: trades arm and run unattended, so a closed trade
 * with no feedback is flagged *awaiting review* and can be annotated at any time. Nothing here is ever
 * required to close or record a trade, and nothing here is on a hot path.
 *
 * **`awaitingReview` is re-read, never patched locally.** The server derives it from whether any
 * *operator*-authored entry exists, which is exactly what stops it drifting from the entries beside it.
 * Flipping a local copy after a write would reintroduce the drift the derivation exists to prevent, so a
 * successful write triggers a fresh read instead.
 *
 * **A refusal leaves the form as it was.** "Feedback can only be attached to a closed trade" is an answer
 * (R-11), so the reason is shown and the operator's words stay on screen — clearing a rejected comment throws
 * away what they wrote.
 */
export interface TradeFeedbackPanelProps {
  readonly tradeId: string;
}

/**
 * The quick tags the wireframe draws. Module-level and frozen, so the array identity is stable across renders
 * — an inline literal here would be a new array every render and a fresh dependency for anything that reads it.
 * They are a **shortcut, not a vocabulary**: the server stores free-form tags and defines no taxonomy, so the
 * comment field remains the primary input and nothing here constrains what may be recorded.
 */
const QUICK_TAGS = ['Chased', 'Wrong session time', 'Followed plan', 'Good loss'] as const;

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly summary: TradeFeedbackSummary };

export function TradeFeedbackPanel({ tradeId }: TradeFeedbackPanelProps) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [comment, setComment] = useState('');
  const [emotionalState, setEmotionalState] = useState('');
  const [tags, setTags] = useState<readonly string[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    void getTradeFeedback(tradeId).then((result) => {
      if (!mounted.current) {
        return;
      }
      setState(
        result.ok
          ? { kind: 'loaded', summary: result.data }
          : { kind: 'error', message: result.kind === 'refused' ? result.reason : result.error },
      );
    });
  }, [tradeId]);

  useEffect(() => {
    load();
  }, [load]);

  const trimmedComment = comment.trim();
  const trimmedState = emotionalState.trim();
  // Mirrors the server's own `CK_TradeFeedback_HasContent`: an entirely empty submission is refused there, so
  // the control is disabled here rather than sending a request that cannot succeed.
  const hasContent = trimmedComment !== '' || trimmedState !== '' || tags.length > 0;

  const submit = useCallback(() => {
    setSubmitting(true);
    setRefusal(null);
    void addTradeFeedback(tradeId, {
      comment: trimmedComment === '' ? null : trimmedComment,
      emotionalState: trimmedState === '' ? null : trimmedState,
      tags: [...tags],
    }).then((result) => {
      if (!mounted.current) {
        return;
      }
      setSubmitting(false);
      if (!result.ok) {
        setRefusal(result.kind === 'refused' ? result.reason : result.error);
        return;
      }
      setComment('');
      setEmotionalState('');
      setTags([]);
      load();
    });
  }, [load, tags, tradeId, trimmedComment, trimmedState]);

  if (state.kind === 'loading') {
    return <LoadingState label="Loading the feedback" fullHeight={false} />;
  }

  if (state.kind === 'error') {
    return (
      <Stack spacing={1} sx={{ py: 1 }}>
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {state.message}
        </Typography>
        <Box>
          <Button variant="outlined" size="small" onClick={load}>
            Try again
          </Button>
        </Box>
      </Stack>
    );
  }

  return (
    <Stack spacing={1.5} data-testid="trade-feedback">
      {state.summary.awaitingReview ? (
        <Box>
          <Chip size="small" color="warning" label="Awaiting review" />
        </Box>
      ) : null}

      {state.summary.entries.map((entry) => (
        <Stack key={entry.id} spacing={0.5}>
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            {entry.author === 'Operator' ? 'You' : entry.author}
          </Typography>
          {entry.comment === null ? null : <Typography variant="body2">{entry.comment}</Typography>}
          <Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap', gap: 0.5 }}>
            {entry.tags.map((tag) => (
              <Chip key={tag} size="small" variant="outlined" label={tag} />
            ))}
            {entry.emotionalState === null ? null : (
              <Chip size="small" variant="outlined" label={entry.emotionalState} />
            )}
          </Stack>
        </Stack>
      ))}

      {refusal === null ? null : (
        <Alert severity="warning" icon={false}>
          {refusal}
        </Alert>
      )}

      <Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap', gap: 0.5 }}>
        {QUICK_TAGS.map((tag) => (
          <Chip
            key={tag}
            size="small"
            clickable
            component="button"
            type="button"
            variant={tags.includes(tag) ? 'filled' : 'outlined'}
            color={tags.includes(tag) ? 'primary' : 'default'}
            label={tag}
            onClick={() => {
              setTags((current) =>
                current.includes(tag)
                  ? current.filter((candidate) => candidate !== tag)
                  : [...current, tag],
              );
            }}
          />
        ))}
      </Stack>

      <TextField
        label="Comment"
        value={comment}
        onChange={(event) => setComment(event.target.value)}
        multiline
        minRows={2}
        size="small"
        slotProps={{ htmlInput: { maxLength: COMMENT_MAX_LENGTH } }}
      />
      <TextField
        label="State of mind"
        value={emotionalState}
        onChange={(event) => setEmotionalState(event.target.value)}
        size="small"
        slotProps={{ htmlInput: { maxLength: EMOTIONAL_STATE_MAX_LENGTH } }}
      />

      <Box>
        <Button
          variant="outlined"
          size="small"
          disabled={!hasContent || submitting}
          onClick={submit}
        >
          Add feedback
        </Button>
      </Box>
    </Stack>
  );
}
