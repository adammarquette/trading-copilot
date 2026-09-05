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
  TURN_IN_FLIGHT_LAYER,
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

/**
 * How long a streaming draft may sit with no new delta before it is treated as abandoned.
 *
 * **It is a backstop now, not the mechanism.** It was written for the faulted turn that sent no terminator at all;
 * gh#1107 gave that turn a real one (`realtimeChatTurnFaulted`), which retires the draft at the moment the fault
 * happens rather than 30 seconds later. It is kept deliberately, for two states the terminator does not cover.
 * (1) That push is **fail-open** — a hub fault or a dropped frame on a live socket loses it outright, and nothing
 * else would then retire the draft until this connection's next send or a reconnect. (2) A gap this long occurs
 * inside a turn that is still running: only round 1 streams, so a TOOL-USING turn emits its preamble, then goes
 * quiet for several non-streaming `CompleteAsync` rounds and the tool calls between them
 * (`ChatTurnService.StreamAsync`); nothing is feeding the draft, and the settled push restores the real answer the
 * moment the turn lands.
 */
const DRAFT_IDLE_MS = 30_000;

/**
 * How long after a locally settled turn a chunk may still be one of its stragglers (gh#1085). The server had
 * already sent every delta before it wrote the REST response, so this is a delivery skew, not a duration.
 */
const STRAGGLER_WINDOW_MS = 15_000;

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

/**
 * Appends a message unless its id is already present -- the fold used for cross-connection pushes and for the
 * reconnect re-read. It dedupes by ID ALONE and never compares content, which is sound only because a thread is
 * append-only: the chat endpoints expose no update or delete for a message (`ChatEndpoints`), so a row cannot
 * change under a fold or vanish from it. An edit path would make a folded read permanently wrong rather than merely
 * stale, and would have to be added here first.
 */
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
 * **Streaming, on every screen.** `POST /conversations/{id}/turns` (gh#906) does not answer until the whole turn
 * completes, but the server pushes each token delta over the realtime hub as it generates. This renders those via
 * {@link useRealtime}'s `onChatChunk` as a growing draft bubble, then retires it for the settled {@link ChatMessage}
 * -- the chunk stream is presentation-only and best-effort (a dropped chunk is never fatal); the REST response and
 * the settled message are the turn's source of truth, never the accumulated deltas.
 *
 * Both pushes are per-OWNER (`Clients.User`), i.e. delivered to EVERY connection the operator has open, which
 * ADR-0021 says is the point of them ("the push serves the owner's *other* connections" -- the multi-screen
 * workspace, ADR-0006). So the draft is driven by the chunk stream itself, never by whether *this* mounted instance
 * called `send()` (gh#1103): a second screen watching the same conversation streams it too. What terminates a turn's
 * stream is its **settled assistant message push** -- the server sends it after the last delta and one connection
 * receives both in send order (chat pushes are live-only, outside the resume replay, so no catch-up can reorder
 * them), which is why it can both retire the draft here and re-arm the stream for the next turn. The one thing that
 * can arrive out of order is this connection's own REST response, which is why a turn settled locally before its
 * push arrives suppresses the chunks still in flight behind it (gh#1085) rather than resurrecting the bubble under
 * an answer already rendered.
 *
 * **One draft per conversation, and the server keeps it true.** A delta's only correlation key is the conversation
 * id, and `RealtimeChatChunk` documents "one in-flight turn per conversation" -- which the server now *enforces*
 * (gh#1106): a second concurrent turn on a conversation is refused with a 409 carrying a reason, rather than
 * streaming into the same undifferentiated draft. So one draft per conversation is the right shape, not a
 * limitation to work around, and no per-turn id was added to the wire. A turn that FAULTS is terminated too
 * (gh#1107): `TurnAsync` returns 422 to the sender and pushes `realtimeChatTurnFaulted` to every other connection,
 * which retires the draft here and shows the reason. What is left is a *lost* push -- these are fail-open, so a
 * dropped terminator (or dropped settled message) leaves a draft standing, which the {@link DRAFT_IDLE_MS} backstop
 * bounds and a reconnect clears; a turn started inside that window still appends to the abandoned draft, pinned by
 * test rather than left to be rediscovered. In every case the settled thread stays correct -- only the live draft
 * is affected, which is the degraded state R-19 / ADR-0013's connection indicator exists to declare.
 *
 * **The optimistic operator turn is provisional.** It renders immediately (temp id {@link PENDING_USER_ID}) so the
 * operator sees what they sent while the turn is in flight, and is replaced by the server's copy on success. On a
 * refusal it is DROPPED rather than left standing, on every refusal, though not for one reason. A 429 (governor)
 * and the 409 for a turn already in flight (gh#1106) persist nothing at all, so there is nothing to reconcile
 * against. A 422 (faulted turn) DOES persist the operator's turn server-side -- and so does the OTHER 409, a lost
 * sequence race on the assistant append, which this client cannot tell from the in-flight refusal by status alone
 * (see `sendChatTurn`) -- but their persisted row has no id this client knows, so rendering an un-reconciled bubble
 * would claim a state neither confirmed nor retractable. The typed text is kept in the composer (not cleared) so
 * retrying costs nothing, which is the whole affordance on an in-flight 409: retry once the other turn lands.
 *
 * **A refused send must not disturb the turn that refused it.** The in-flight 409 is returned *precisely when
 * another turn is streaming*, so this connection ran nothing and the draft on screen is that other turn's. It is
 * therefore the one refusal that does NOT retire the draft or arm the straggler suppression -- doing either would
 * discard a live answer and then blank it mid-sentence, which is the symptom gh#1106 exists to remove, reached
 * through the refusal that was supposed to prevent it. `send()` blanks the draft optimistically (it cannot know
 * yet), and this refusal puts it back; the restore is exact because at most one turn is in flight, so the true
 * draft is what was there plus whatever arrived during the round-trip. It is skipped when that turn TERMINATED
 * inside the round-trip (`settledTurnsRef` moved) -- the blank was right after all, and restoring dead text would
 * stand half an answer under the finished one. Keyed on the refusal envelope's `layer`
 * ({@link TURN_IN_FLIGHT_LAYER}), because the endpoint's other 409 -- a lost sequence race -- means the opposite
 * and the status alone cannot separate them.
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
  // The chunk stream's guards (see the module note on the streaming draft):
  //   `settledTurnsRef` counts the settled assistant pushes seen for THIS conversation -- each one terminates its
  //     turn's chunk stream on this connection, so it is also what re-opens the gate below.
  //   `stragglersRef` is true only in the window where this connection settled a turn over REST but has not yet
  //     seen that turn's terminating push, i.e. the only window a straggling chunk can still arrive in (gh#1085).
  const settledTurnsRef = useRef(0);
  const stragglersRef = useRef(false);
  const stragglerTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Mirrors the rendered draft so `send()` can snapshot it synchronously (it is not in that callback's deps, and a
  // closed-over `streaming` would be stale). Only the refusal path below reads it -- see there for why.
  const streamingRef = useRef<{ readonly text: string } | null>(null);

  useEffect(() => {
    streamingRef.current = streaming;
  }, [streaming]);

  // Arms / disarms the straggler suppression, TIME-BOUNDED. A straggler is a chunk still in flight behind a turn
  // this connection already settled, so it is a matter of seconds. A faulted turn now sends a terminator that
  // disarms it (gh#1107), and a settled one always did -- but both are fail-open pushes, so the expiry stays:
  // a suppression left armed waiting on a terminator that was DROPPED would silently cost this connection the
  // NEXT turn's draft, which is gh#1103's own bug re-entered through the error path.
  const suppressStragglers = useCallback((suppress: boolean) => {
    if (stragglerTimer.current !== null) {
      clearTimeout(stragglerTimer.current);
      stragglerTimer.current = null;
    }
    stragglersRef.current = suppress;
    if (suppress) {
      stragglerTimer.current = setTimeout(() => {
        stragglersRef.current = false;
        stragglerTimer.current = null;
      }, STRAGGLER_WINDOW_MS);
    }
  }, []);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      if (stragglerTimer.current !== null) {
        clearTimeout(stragglerTimer.current);
        stragglerTimer.current = null;
      }
    };
  }, []);

  // Retire a draft nothing is feeding any more. `streaming` is a fresh object per delta, so this re-arms on every
  // chunk and only fires once the stream has actually gone quiet. It is now a BACKSTOP rather than the mechanism
  // (see {@link DRAFT_IDLE_MS}): a faulted turn pushes its own terminator (gh#1107) and a settled one always did,
  // so what is left here is a terminator that was LOST -- both pushes are fail-open -- plus a live tool-using
  // turn's quiet stretch. It bounds the damage: the draft cannot outlive its turn by more than the idle window,
  // and a delta arriving after it starts a NEW draft instead of welding onto an abandoned one. It still does not
  // CLOSE that residual gap -- a turn started inside the window appends to the stale draft, because a delta
  // carries no turn identity -- which is pinned by test rather than left to be found.
  useEffect(() => {
    if (streaming === null) {
      return;
    }
    const timer = setTimeout(() => setStreaming(null), DRAFT_IDLE_MS);
    return () => clearTimeout(timer);
  }, [streaming]);

  // `conversationId` only ever changes via a remount (the host keys this component by it -- see the module note),
  // so the mount read runs exactly once per mount and the already-`loading` initial state needs no reset here.
  //
  // `background` separates the two readers. The MOUNT read owns the whole surface: nothing is rendered yet, so a
  // failure has to become the error screen. A BACKGROUND refresh (the reconnect reconcile) must never do that --
  // a socket blip is exactly when the follow-up GET is most likely to fail too, and taking a working thread and
  // its composer away because a reconcile read failed is the "reconcile signal nuking a working surface" ADR-0021
  // rules out (gh#760) and the suggestion panel / blotter avoid. It keeps what is rendered instead. Its success
  // path FOLDS rather than replaces, for the same reason `onChatMessage` does: a push that lands while the read is
  // in flight would otherwise be overwritten by the older list and, pushes being live-only, stay missing until a
  // remount. A thread is append-only, so folding is monotone -- it cannot resurrect a row the server dropped
  // because the server drops none.
  const load = useCallback(
    (background = false) => {
      void getConversation(conversationId)
        .then((result) => {
          if (!mounted.current) {
            return;
          }
          if (!result.ok) {
            if (!background) {
              setState({
                kind: 'error',
                message: result.kind === 'refused' ? result.reason : result.error,
              });
            }
            return;
          }
          // System rows are grounding/instructions, never an operator-visible turn (see the module note).
          const messages = result.data.messages.filter(
            (message) => message.role !== ChatRole.System,
          );
          setState((current) =>
            background && current.kind === 'loaded'
              ? { kind: 'loaded', messages: messages.reduce(foldIn, current.messages) }
              : { kind: 'loaded', messages },
          );
        })
        .catch(() => {
          if (mounted.current && !background) {
            setState({ kind: 'error', message: 'The conversation could not be loaded.' });
          }
        });
    },
    [conversationId],
  );

  useEffect(load, [load]);

  const retry = useCallback(() => {
    setState({ kind: 'loading' });
    load();
  }, [load]);

  // Cross-connection reconciliation (gh#906, ADR-0006/ADR-0021): a turn taken on another window/tab is pushed here
  // too. Filtered to THIS conversation and folded in by id, so a redundant delivery (or our own turn's push racing
  // its REST response) never duplicates a row.
  const { onChatMessage, onChatChunk, onChatTurnFaulted, onResync } = useRealtime();
  useEffect(
    () =>
      onChatMessage((message) => {
        if (message.conversationId !== conversationId || message.role === ChatRole.System) {
          return;
        }
        // A settled ASSISTANT message terminates its turn's chunk stream on this connection -- the server pushes
        // it after the last delta (ChatEndpoints.TurnAsync) and both travel one connection in send order, so no
        // chunk of that turn can follow it. It therefore both retires the live draft (on EVERY connection, not
        // just the sender's -- the stale-duplicate half of gh#1103) and re-opens the gate for the next turn.
        // Narrowed to Assistant because a user-role push, were one ever added (today `TurnAsync` pushes only the
        // assistant message, and `AppendAsync` pushes nothing at all), would be the operator's own turn arriving
        // from another screen -- it PRECEDES the answer being generated, so retiring a draft on it would blank
        // the very stream it introduces. Defensive, not a description of traffic that exists.
        if (message.role === ChatRole.Assistant) {
          settledTurnsRef.current += 1;
          suppressStragglers(false);
          setStreaming(null);
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
    [onChatMessage, conversationId, suppressStragglers],
  );
  useEffect(
    () =>
      onChatChunk((chunk) => {
        // A chunk is live UNLESS it straggles a turn this connection already settled itself. The gate is a ref,
        // not state: this effect is registered once per mount (see the dependency array) and never re-subscribes,
        // so a `streaming`/`sending` value closed over here would be permanently stale -- a ref is read fresh on
        // every delivery. It is deliberately NOT `sendingRef` (gh#1103): that is only ever true on the tab that
        // itself called send(), while the push is per-owner and exists to serve the operator's OTHER connections
        // (ADR-0021), which is exactly where the "the co-pilot is typing…" draft is worth having. Dropping a
        // straggler is a silent no-op, not a failure: the chunk stream stays fail-open either way (gh#1085).
        if (chunk.conversationId !== conversationId || stragglersRef.current) {
          return;
        }
        setStreaming((current) => ({ text: (current?.text ?? '') + chunk.delta }));
      }),
    [onChatChunk, conversationId],
  );

  // The OTHER terminator (gh#1107). A turn that faults streams its first round and then produces no assistant
  // message at all, so the settled-message push above never comes: before this signal existed, every connection
  // that did not send the turn kept the half-written answer standing with no error and nothing to clear it. This
  // retires it at the same moment the sender's 422 does, and -- R-19 / ADR-0013's honest-states stance, on a
  // screen that just watched an answer stop mid-sentence -- says why when the server gave a reason. It also
  // counts as a settled turn and disarms the straggler suppression, for the same reason the settled-message push
  // does: the turn's chunk stream is over, so a later delta belongs to a NEW turn and must open a fresh draft.
  useEffect(
    () =>
      onChatTurnFaulted((faulted) => {
        if (faulted.conversationId !== conversationId) {
          return;
        }
        settledTurnsRef.current += 1;
        suppressStragglers(false);
        setStreaming(null);
        // A blank/absent reason is not a reason -- rendering an empty alert would be worse than staying silent.
        if (faulted.reason) {
          setSendError(faulted.reason);
        }
      }),
    [onChatTurnFaulted, conversationId, suppressStragglers],
  );

  // A reconnect re-reads the thread (ADR-0021: chat pushes are live-only and outside the resume replay, so a turn
  // that settled while the socket was down is never replayed) and drops whatever draft the dropped socket left
  // stranded -- nothing else would ever terminate it, and half a turn's tokens with a hole in them is not a draft
  // worth keeping. The same discipline the blotter and the chart overlays take on `onResync`, and a BACKGROUND
  // read for the same reason they use one: a failed reconcile keeps the surface, never replaces it with an error
  // screen. The draft is dropped either way -- it is stale on any reconnect -- but the RE-READ is skipped while a
  // turn THIS connection sent is in flight: that turn travels over HTTP, unaffected by the socket, and its own
  // settle is the authoritative reconcile (it drops the optimistic row and folds the real pair), so re-reading
  // underneath it would only blank the operator's own optimistic turn.
  useEffect(
    () =>
      onResync(() => {
        suppressStragglers(false);
        setStreaming(null);
        if (!sendingRef.current) {
          load(true);
        }
      }),
    [onResync, load, suppressStragglers],
  );

  const send = useCallback(() => {
    const content = draft.trim();
    if (content.length === 0 || content.length > CONTENT_MAX_LENGTH || sendingRef.current) {
      return;
    }

    sendingRef.current = true;
    setSending(true);
    setSendError(null);
    // A new turn begins, so whatever the last one left behind goes and its chunks are live again -- UNLESS this
    // send turns out to be refused because another turn is already in flight (gh#1106), in which case the draft
    // just blanked was that turn's and is still growing. This connection cannot know which until the response, so
    // it blanks optimistically and keeps the text to put back; the restore is exact because there is at most one
    // turn in flight, so the true draft is simply what was here plus whatever arrived in the meantime.
    const draftAtSend = streamingRef.current;
    setStreaming(null);
    // The snapshot the settle path compares against to tell "this turn is still streaming to me" from "already
    // terminated".
    suppressStragglers(false);
    const settledTurnsAtStart = settledTurnsRef.current;
    setState((current) =>
      current.kind === 'loaded'
        ? {
            kind: 'loaded',
            messages: [
              ...current.messages,
              {
                id: PENDING_USER_ID,
                conversationId,
                // One past the highest sequence this client has seen -- exactly the value the server will assign
                // the real user turn (TryAppendAsync allocates max+1). This has to track the CURRENT max rather
                // than a fixed sentinel like MAX_SAFE_INTEGER: the assistant's own hub push for THIS turn can
                // arrive (onChatMessage, with its real, small sequence) before this REST call settles -- see the
                // module note -- and a fixed huge sentinel would then sort the pending bubble AFTER the reply it
                // is supposed to precede.
                sequence:
                  current.messages.reduce((max, existing) => Math.max(max, existing.sequence), 0) +
                  1,
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

        // REFUSED BECAUSE A TURN WAS ALREADY IN FLIGHT (gh#1106): this send ran nothing at all, so none of the
        // draft bookkeeping below applies to it -- and both halves of that bookkeeping would actively damage the
        // OTHER turn, the one this refusal proves is streaming right now. Retiring the draft would discard an
        // answer being rendered correctly; arming the straggler suppression would then drop that turn's deltas for
        // the whole suppression window and re-open its draft from empty mid-answer, which is precisely the symptom
        // the server-side guard exists to remove. So instead the optimistic blank above is UNDONE: the true draft
        // is the text that was there before the blank plus whatever arrived during the round-trip, which is exact
        // because at most one turn is in flight. (Both null stays null -- never conjure an empty bubble.)
        const refusedAsAlreadyInFlight =
          !result.ok && result.kind === 'refused' && result.layer === TURN_IN_FLIGHT_LAYER;
        if (refusedAsAlreadyInFlight) {
          // ...but ONLY while the turn that draft belongs to is still running. If its terminator arrived inside
          // this round-trip -- a settled assistant push, or a faulted one (gh#1107); both bump `settledTurnsRef`
          // and retire the draft -- then the blank was correct after all and the snapshot is dead text. Putting it
          // back would stand a half-written copy of the answer now rendered above it for the whole idle window,
          // which is the stale-draft state gh#1107 exists to remove, re-entered through this refusal. Leave
          // `streaming` untouched rather than clearing it: by then it may hold a NEW turn's first deltas.
          if (settledTurnsRef.current === settledTurnsAtStart) {
            setStreaming((current) =>
              draftAtSend === null && current === null
                ? null
                : { text: (draftAtSend?.text ?? '') + (current?.text ?? '') },
            );
          }
        } else {
          setStreaming(null);
          // Close the gate only if this turn's terminating push has NOT already been seen. If it has, the turn's
          // chunk stream is over and every later chunk belongs to a NEW turn -- possibly one taken on another
          // screen, which would never re-arm anything here (gh#1103). If it has not, the REST response has simply
          // won the race and further chunks are stragglers of the answer already rendered (gh#1085); the push that
          // terminates them re-opens the gate. A push lost outright leaves this connection closed until its own
          // next send -- the same degraded state that already costs it the settled message, which R-19's
          // connection indicator is what surfaces.
          suppressStragglers(settledTurnsRef.current === settledTurnsAtStart);
        }

        if (!result.ok) {
          // Drop the optimistic row on every refusal, though the reasons differ (see the module note): a 429 and
          // the in-flight 409 persisted nothing, while a 422 -- and a sequence-race 409 on the assistant append,
          // which is indistinguishable here by status -- persisted a user turn whose id this client does not know.
          // Either way an un-reconciled row would claim a state we cannot confirm. The typed text survives in the
          // composer (draft is untouched) so retrying costs nothing.
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
                // foldIn, not an unconditional append: the server pushes the assistant's message over the owner-
                // scoped hub -- to EVERY one of the operator's connections, including this sender's own -- BEFORE
                // returning this REST response (ChatEndpoints.TurnAsync), so onChatMessage racing ahead of this
                // resolve is the ordinary case, not a rare one. Folding by id is what keeps that race from
                // rendering the same turn twice under a colliding React key.
                messages: [result.data.userMessage, result.data.assistantMessage].reduce(
                  foldIn,
                  current.messages.filter((message) => message.id !== PENDING_USER_ID),
                ),
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
        suppressStragglers(settledTurnsRef.current === settledTurnsAtStart); // as above
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
  }, [conversationId, draft, suppressStragglers]);

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
