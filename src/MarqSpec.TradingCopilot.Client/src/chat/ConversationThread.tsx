import SendIcon from '@mui/icons-material/Send';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { type FormEvent, useCallback, useEffect, useRef, useState } from 'react';

import {
  CONTENT_MAX_LENGTH,
  ChatRole,
  type ChatMessage,
  type ChatRoleValue,
  getConversation,
  sendChatTurn,
} from '../api/chat';
import { EmptyState } from '../components/EmptyState';
import { LoadingState } from '../components/LoadingState';
import { useRealtime } from '../realtime/RealtimeProvider';

export interface ConversationThreadProps {
  readonly conversationId: string;
}

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'loaded'; readonly messages: readonly ChatMessage[] };

/** The temporary id an optimistic operator turn carries until the server assigns a real one. */
const PENDING_USER_ID = 'pending-user-turn';

function roleLabel(role: ChatMessage['role']): string {
  switch (role) {
    case ChatRole.User:
      return 'user';
    case ChatRole.Assistant:
      return 'assistant';
    case ChatRole.System:
      return 'system';
    default:
      return 'unknown';
  }
}

/** Appends a message unless its id is already present -- the fold used for both cross-connection pushes and reconcile. */
function foldIn(messages: readonly ChatMessage[], incoming: ChatMessage): readonly ChatMessage[] {
  if (messages.some((existing) => existing.id === incoming.id)) {
    return messages;
  }
  return [...messages, incoming].sort((a, b) => a.sequence - b.sequence);
}

/**
 * One conversation's thread and composer (gh#1063, R-6) -- the increment behind #323's dedicated `/chat` surface.
 * Mounted **keyed by `conversationId`** by its host ({@link ChatSurface}), so switching threads is a fresh mount
 * rather than an in-place prop swap: no streaming draft, in-flight send, or stale `mounted` guard can leak from one
 * conversation into another (the same discipline `WorkspaceSurface` uses keying `Blotter` by account).
 *
 * **Streaming.** `POST /conversations/{id}/turns` (gh#906) does not answer until the whole turn completes, but the
 * server pushes each token delta over the realtime hub as it generates. This renders those via {@link useRealtime}'s
 * `onChatChunk` as a growing draft bubble, then swaps it for the settled {@link ChatMessage} pair the REST call
 * resolves with -- the chunk stream is presentation-only and best-effort (a dropped chunk is never fatal); the REST
 * response is the turn's source of truth, never the accumulated deltas.
 *
 * **The optimistic operator turn is provisional.** It renders immediately (temp id {@link PENDING_USER_ID}) so the
 * operator sees what they sent while the turn is in flight, and is replaced by the server's copy on success. On a
 * refusal it is DROPPED rather than left standing: a 429 (governor) persists nothing at all, and a 422 (faulted
 * turn) does persist the operator's turn server-side but this client does not know its real id -- rendering an
 * un-reconciled row would claim a state neither confirmed nor retractable. The typed text is kept in the composer
 * (not cleared) so retrying costs nothing.
 *
 * **Untrusted content, both roles.** A message's `content` -- assistant or operator -- is rendered as plain text via
 * ordinary JSX interpolation, never `dangerouslySetInnerHTML` or any markdown/HTML interpreter: the always-on news
 * grounding (gh#995, ADR-0027) that can shape an assistant reply is handed to the model as untrusted user-role data
 * server-side, and this is the presentation-layer half of that contract -- a reply is displayed, never parsed as
 * markup or re-issued as an instruction. A `System`-role row (grounding / instructions) is filtered out entirely:
 * it is never something the operator typed or should read as a turn.
 */
export function ConversationThread({ conversationId }: ConversationThreadProps) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [streaming, setStreaming] = useState<{ readonly text: string } | null>(null);
  const mounted = useRef(true);
  const sendingRef = useRef(false); // synchronous guard: `sending` state lags a tick behind a rapid double-submit

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  // `conversationId` only ever changes via a remount (the host keys this component by it -- see the module note),
  // so this effect runs exactly once per mount and the already-`loading` initial state needs no reset here.
  const load = useCallback(() => {
    void getConversation(conversationId)
      .then((result) => {
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
        // System rows are grounding/instructions, never an operator-visible turn (see the module note).
        setState({
          kind: 'loaded',
          messages: result.data.messages.filter((message) => message.role !== ChatRole.System),
        });
      })
      .catch(() => {
        if (mounted.current) {
          setState({ kind: 'error', message: 'The conversation could not be loaded.' });
        }
      });
  }, [conversationId]);

  useEffect(load, [load]);

  const retry = useCallback(() => {
    setState({ kind: 'loading' });
    load();
  }, [load]);

  // Cross-connection reconciliation (gh#906, ADR-0006/ADR-0021): a turn taken on another window/tab is pushed here
  // too. Filtered to THIS conversation and folded in by id, so a redundant delivery (or our own turn's push racing
  // its REST response) never duplicates a row.
  const { onChatMessage, onChatChunk } = useRealtime();
  useEffect(
    () =>
      onChatMessage((message) => {
        if (message.conversationId !== conversationId || message.role === ChatRole.System) {
          return;
        }
        setState((current) =>
          current.kind === 'loaded'
            ? {
                kind: 'loaded',
                messages: foldIn(current.messages, {
                  id: message.messageId,
                  conversationId: message.conversationId,
                  sequence: message.sequence,
                  // The hub carries `role` as a plain number (see RealtimeChatMessage); the guard just above already
                  // refused System (3), and Unknown (0) never reaches the wire (a DB check refuses it server-side),
                  // so what remains is User or Assistant -- narrowing here is sound, not an assumption.
                  role: message.role as ChatRoleValue,
                  content: message.content,
                  createdAt: message.at,
                }),
              }
            : current,
        );
      }),
    [onChatMessage, conversationId],
  );
  useEffect(
    () =>
      onChatChunk((chunk) => {
        if (chunk.conversationId !== conversationId) {
          return;
        }
        setStreaming((current) => ({ text: (current?.text ?? '') + chunk.delta }));
      }),
    [onChatChunk, conversationId],
  );

  const send = useCallback(() => {
    const content = draft.trim();
    if (content.length === 0 || content.length > CONTENT_MAX_LENGTH || sendingRef.current) {
      return;
    }

    sendingRef.current = true;
    setSending(true);
    setSendError(null);
    setStreaming(null);
    setState((current) =>
      current.kind === 'loaded'
        ? {
            kind: 'loaded',
            messages: [
              ...current.messages,
              {
                id: PENDING_USER_ID,
                conversationId,
                sequence: Number.MAX_SAFE_INTEGER, // always sorts last among real (small, sequential) sequences
                role: ChatRole.User,
                content,
                createdAt: new Date().toISOString(),
              },
            ],
          }
        : current,
    );

    void sendChatTurn(conversationId, content)
      .then((result) => {
        if (!mounted.current) {
          return;
        }
        sendingRef.current = false;
        setSending(false);
        setStreaming(null);

        if (!result.ok) {
          // Drop the optimistic row: a 429 persisted nothing, and a 422's persisted user turn has no id this
          // client knows -- an un-reconciled row would claim a state we cannot confirm. The typed text survives
          // in the composer (draft is untouched) so retrying costs nothing.
          setState((current) =>
            current.kind === 'loaded'
              ? {
                  kind: 'loaded',
                  messages: current.messages.filter((message) => message.id !== PENDING_USER_ID),
                }
              : current,
          );
          setSendError(result.kind === 'refused' ? result.reason : result.error);
          return;
        }

        setDraft('');
        setState((current) =>
          current.kind === 'loaded'
            ? {
                kind: 'loaded',
                messages: [
                  ...current.messages.filter((message) => message.id !== PENDING_USER_ID),
                  result.data.userMessage,
                  result.data.assistantMessage,
                ].sort((a, b) => a.sequence - b.sequence),
              }
            : current,
        );
      })
      .catch(() => {
        // A surface must not be strandable by a throw it did not anticipate (gh#973 class).
        if (!mounted.current) {
          return;
        }
        sendingRef.current = false;
        setSending(false);
        setStreaming(null);
        setState((current) =>
          current.kind === 'loaded'
            ? {
                kind: 'loaded',
                messages: current.messages.filter((message) => message.id !== PENDING_USER_ID),
              }
            : current,
        );
        setSendError('The message could not be sent.');
      });
  }, [conversationId, draft]);

  const handleSubmit = useCallback(
    (event: FormEvent<HTMLFormElement>) => {
      event.preventDefault();
      send();
    },
    [send],
  );

  if (state.kind === 'loading') {
    return <LoadingState label="Loading the conversation" />;
  }

  if (state.kind === 'error') {
    return (
      <Stack spacing={2} data-testid="thread-error" sx={{ p: 2 }}>
        <Alert severity="error" role="alert">
          {state.message}
        </Alert>
        <Box>
          <Button variant="outlined" onClick={retry}>
            Try again
          </Button>
        </Box>
      </Stack>
    );
  }

  const composer = (
    <Box
      component="form"
      onSubmit={handleSubmit}
      sx={{ p: 1.5, borderTop: 1, borderColor: 'divider' }}
    >
      {sendError ? (
        <Alert severity="error" role="alert" sx={{ mb: 1 }} onClose={() => setSendError(null)}>
          {sendError}
        </Alert>
      ) : null}
      <Stack direction="row" spacing={1}>
        <TextField
          fullWidth
          size="small"
          label="Message"
          placeholder="Ask the co-pilot — quotes, journal, edit a rule…"
          value={draft}
          disabled={sending}
          onChange={(event) => setDraft(event.target.value)}
        />
        <Button
          type="submit"
          variant="contained"
          endIcon={<SendIcon />}
          disabled={sending || draft.trim().length === 0}
        >
          Send
        </Button>
      </Stack>
    </Box>
  );

  return (
    <Box
      data-testid="conversation-thread"
      sx={{ height: '100%', display: 'flex', flexDirection: 'column', minHeight: 0 }}
    >
      <Box sx={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
        {state.messages.length === 0 && streaming === null ? (
          <EmptyState
            title="Nothing here yet"
            description="Ask about a setup, a rule, or a day just traded."
            tag="R-6"
          />
        ) : (
          <Stack spacing={1} sx={{ p: 2 }}>
            {state.messages.map((message) => (
              <MessageBubble key={message.id} message={message} />
            ))}
            {streaming !== null ? (
              <Paper
                variant="outlined"
                data-testid="chat-draft"
                data-role="assistant"
                sx={{ px: 1.5, py: 1, maxWidth: '80%', alignSelf: 'flex-start', opacity: 0.75 }}
              >
                <Typography variant="body2">{streaming.text}</Typography>
              </Paper>
            ) : null}
          </Stack>
        )}
      </Box>
      {composer}
    </Box>
  );
}

function MessageBubble({ message }: { readonly message: ChatMessage }) {
  const mine = message.role === ChatRole.User;
  return (
    <Paper
      variant="outlined"
      data-testid="chat-message"
      data-role={roleLabel(message.role)}
      sx={{
        px: 1.5,
        py: 1,
        maxWidth: '80%',
        alignSelf: mine ? 'flex-end' : 'flex-start',
        bgcolor: mine ? 'primary.main' : 'background.paper',
        color: mine ? 'primary.contrastText' : 'text.primary',
      }}
    >
      {/* Plain text interpolation only -- see the module note on the untrusted-content boundary. */}
      <Typography variant="body2">{message.content}</Typography>
    </Paper>
  );
}
