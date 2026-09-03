import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import { useCallback, useEffect, useRef, useState } from 'react';

import { type Conversation, createConversation, listConversations } from '../api/chat';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
import type { Destination } from '../navigation/destinations';
import { ConversationList } from './ConversationList';
import { ConversationThread } from './ConversationThread';

export interface ChatSurfaceProps {
  readonly destination: Destination;
}

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly conversations: readonly Conversation[] };

/**
 * The `/chat` surface (gh#1063, #323): the operator's conversation list beside the selected thread. Increment 8 of
 * gh#18's roadmap -- the piece that finally makes the merged persistence / CRUD / grounded-turn / retrieval /
 * rerank stack (gh#898/#901/#906/#925/#930/#975/#995) reachable, since `/chat` was a placeholder route until now.
 *
 * **Selection, not routing.** The thread shown is local state here rather than a `/chat/:id` sub-route -- the
 * navigation table (`destinations.ts`) is flat by design (one destination, one path), the same reason
 * `AccountSwitcher` resolves its active account locally rather than through the URL. {@link ConversationThread} is
 * mounted **keyed by the selected id**, so switching conversations is a fresh mount (see its own module note).
 *
 * **Creating auto-selects.** A brand-new conversation has no messages yet, so opening straight into it -- rather
 * than leaving the operator on whatever was selected before -- is what "start a conversation" means; the empty
 * thread's own {@link EmptyState} then prompts the first message.
 */
export function ChatSurface({ destination }: ChatSurfaceProps) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    void listConversations().then((result) => {
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
      setState({ kind: 'loaded', conversations: result.data });
      // Auto-select the most recent conversation (the server's own most-recent-first order) on first load only --
      // an explicit selection already in place (from a prior load, or a just-created conversation) is never
      // overridden by a background refresh.
      setSelectedId((current) => current ?? result.data[0]?.id ?? null);
    });
  }, []);

  // The initial state is already `loading` -- no synchronous setState needed here, only the read itself.
  useEffect(load, [load]);

  const retry = useCallback(() => {
    setState({ kind: 'loading' });
    load();
  }, [load]);

  const onNew = useCallback(() => {
    setCreating(true);
    setCreateError(null);
    void createConversation()
      .then((result) => {
        if (!mounted.current) {
          return;
        }
        setCreating(false);
        if (!result.ok) {
          setCreateError(result.kind === 'refused' ? result.reason : result.error);
          return;
        }
        setState((current) =>
          current.kind === 'loaded'
            ? { kind: 'loaded', conversations: [result.data, ...current.conversations] }
            : current,
        );
        setSelectedId(result.data.id);
      })
      .catch(() => {
        if (mounted.current) {
          setCreating(false);
          setCreateError('The conversation could not be created.');
        }
      });
  }, []);

  return (
    <Box
      data-testid="surface"
      data-surface={destination.id}
      sx={{ height: '100%', display: 'flex' }}
    >
      <Box
        sx={{
          width: 280,
          minWidth: 240,
          borderColor: 'divider',
          borderStyle: 'solid',
          borderWidth: '0 1px 0 0',
        }}
      >
        {state.kind === 'loading' ? (
          <LoadingState label="Loading conversations" />
        ) : state.kind === 'error' ? (
          <Stack spacing={2} data-testid="conversations-error" sx={{ p: 2 }}>
            <Alert severity="error" role="alert">
              {state.message}
            </Alert>
            <Box>
              <Button variant="outlined" size="small" onClick={retry}>
                Try again
              </Button>
            </Box>
          </Stack>
        ) : (
          <ConversationList
            conversations={state.conversations}
            selectedId={selectedId}
            onSelect={setSelectedId}
            onNew={onNew}
            creating={creating}
          />
        )}
      </Box>

      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        {createError ? (
          <Alert severity="error" role="alert" sx={{ m: 1.5 }} onClose={() => setCreateError(null)}>
            {createError}
          </Alert>
        ) : null}
        {selectedId === null ? (
          <EmptyState
            title="Start a conversation"
            description="Ask the co-pilot about a setup, a rule, or a day just traded."
            tag="R-6"
          />
        ) : (
          <ConversationThread key={selectedId} conversationId={selectedId} />
        )}
      </Box>
    </Box>
  );
}
