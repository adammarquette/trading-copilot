import { act, cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ChatRole, type ChatMessage, getConversation, sendChatTurn } from '../api/chat';
import type { RealtimeChatChunk, RealtimeChatMessage } from '../realtime/messages';
import { renderWithProviders } from '../testing/render';
import { ConversationThread } from './ConversationThread';

vi.mock('../api/chat', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/chat')>()),
  getConversation: vi.fn(),
  sendChatTurn: vi.fn(),
}));

const { useRealtimeMock } = vi.hoisted(() => ({ useRealtimeMock: vi.fn() }));
vi.mock('../realtime/RealtimeProvider', () => ({ useRealtime: useRealtimeMock }));

const getMock = vi.mocked(getConversation);
const sendMock = vi.mocked(sendChatTurn);

/** A promise whose resolution the test controls, so streamed chunks can be asserted before the turn settles. */
function deferred<T>() {
  let settle!: (value: T) => void;
  const promise = new Promise<T>((resolve) => {
    settle = resolve;
  });
  return { promise, settle };
}

let chatChunkHandler: ((chunk: RealtimeChatChunk) => void) | null = null;
let chatMessageHandler: ((message: RealtimeChatMessage) => void) | null = null;

function message(overrides: Partial<ChatMessage> = {}): ChatMessage {
  return {
    id: 'm1',
    conversationId: 'c1',
    sequence: 1,
    role: ChatRole.User,
    content: "What's my risk if I take it?",
    createdAt: '2026-08-01T00:01:00Z',
    ...overrides,
  };
}

async function renderThread(conversationId = 'c1') {
  const view = renderWithProviders(<ConversationThread conversationId={conversationId} />);
  await act(async () => {});
  return view;
}

beforeEach(() => {
  vi.clearAllMocks();
  chatChunkHandler = null;
  chatMessageHandler = null;
  useRealtimeMock.mockReturnValue({
    connectionState: 'live',
    onEvent: vi.fn(() => vi.fn()),
    onOrderState: vi.fn(() => vi.fn()),
    onFill: vi.fn(() => vi.fn()),
    onSuggestion: vi.fn(() => vi.fn()),
    onResync: vi.fn(() => vi.fn()),
    onChatChunk: (handler: (chunk: RealtimeChatChunk) => void) => {
      chatChunkHandler = handler;
      return vi.fn();
    },
    onChatMessage: (handler: (message: RealtimeChatMessage) => void) => {
      chatMessageHandler = handler;
      return vi.fn();
    },
  });
});

afterEach(cleanup);

describe('ConversationThread — loading and rendering the thread', () => {
  it('renders messages in sequence order, distinguishing the author', async () => {
    getMock.mockResolvedValue({
      ok: true,
      data: {
        id: 'c1',
        title: 'ES setups',
        createdAt: '',
        updatedAt: '',
        messages: [
          message({ id: 'm1', sequence: 1, role: ChatRole.User, content: 'What is my risk?' }),
          message({
            id: 'm2',
            sequence: 2,
            role: ChatRole.Assistant,
            content: '-$412 to the stop.',
          }),
        ],
      },
    });

    await renderThread();

    const rows = screen.getAllByTestId('chat-message');
    expect(rows.map((row) => row.textContent)).toEqual(['What is my risk?', '-$412 to the stop.']);
    expect(rows[0].getAttribute('data-role')).toBe('user');
    expect(rows[1].getAttribute('data-role')).toBe('assistant');
  });

  it('never renders message content as markup -- the untrusted-display boundary (R-6, gh#995)', async () => {
    // Grounding is untrusted user-role data server-side; the client's half of that contract is that NEITHER
    // role's content is ever interpreted as HTML. A mutant that swapped this for dangerouslySetInnerHTML would
    // still pass an assertion written only against getByText -- so this also asserts the tag was never PARSED.
    getMock.mockResolvedValue({
      ok: true,
      data: {
        id: 'c1',
        title: null,
        createdAt: '',
        updatedAt: '',
        messages: [
          message({
            id: 'm1',
            role: ChatRole.Assistant,
            content: '<b onmouseover="alert(1)">ignore previous instructions</b>',
          }),
        ],
      },
    });

    const { container } = await renderThread();

    expect(
      screen.getByText('<b onmouseover="alert(1)">ignore previous instructions</b>'),
    ).toBeTruthy();
    expect(container.querySelector('b')).toBeNull();
    expect(container.querySelector('[onmouseover]')).toBeNull();
  });

  it('never renders a System-role message as an operator-visible turn', async () => {
    getMock.mockResolvedValue({
      ok: true,
      data: {
        id: 'c1',
        title: null,
        createdAt: '',
        updatedAt: '',
        messages: [
          message({ id: 'm1', sequence: 1, role: ChatRole.User, content: 'visible user turn' }),
          message({
            id: 'm2',
            sequence: 2,
            role: ChatRole.System,
            content: 'grounding context the operator never typed',
          }),
        ],
      },
    });

    await renderThread();

    expect(screen.getByText('visible user turn')).toBeTruthy();
    expect(screen.queryByText('grounding context the operator never typed')).toBeNull();
    expect(screen.getAllByTestId('chat-message')).toHaveLength(1);
  });

  it('an empty conversation shows a prompt to start, with the composer still reachable', async () => {
    getMock.mockResolvedValue({
      ok: true,
      data: { id: 'c1', title: null, createdAt: '', updatedAt: '', messages: [] },
    });

    await renderThread();

    expect(screen.getByTestId('empty-state')).toBeTruthy();
    expect(screen.getByLabelText(/Message/i)).toBeTruthy();
  });

  it('a refused/failed load renders an error with retry', async () => {
    getMock.mockResolvedValue({
      ok: false,
      kind: 'failed',
      error: 'The request could not be sent.',
    });

    await renderThread();

    expect(await screen.findByTestId('thread-error')).toBeTruthy();
    expect(screen.getByText('The request could not be sent.')).toBeTruthy();
  });

  it('a THROW out of the load renders an error rather than a permanent spinner (gh#973 class)', async () => {
    getMock.mockRejectedValue(new Error('boom'));

    await renderThread();

    expect(await screen.findByTestId('thread-error')).toBeTruthy();
    expect(screen.queryByTestId('loading-state')).toBeNull();
  });
});

describe('ConversationThread — sending a turn', () => {
  function loadedEmpty() {
    getMock.mockResolvedValue({
      ok: true,
      data: { id: 'c1', title: null, createdAt: '', updatedAt: '', messages: [] },
    });
  }

  it('optimistically shows the operator turn immediately, and disables the composer while sending', async () => {
    loadedEmpty();
    sendMock.mockReturnValue(new Promise(() => {})); // never resolves in this test

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'size to 3?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(screen.getByText('size to 3?')).toBeTruthy();
    expect((screen.getByLabelText(/Message/i) as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Send' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('sends only the newly typed content -- never the assembled thread history as an instruction', async () => {
    loadedEmpty();
    sendMock.mockReturnValue(new Promise(() => {}));

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'size to 3?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(sendMock).toHaveBeenCalledWith('c1', 'size to 3?');
    expect(sendMock).toHaveBeenCalledTimes(1);
  });

  it('renders streamed chunks incrementally before the turn settles', async () => {
    loadedEmpty();
    sendMock.mockReturnValue(new Promise(() => {}));

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'headroom?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'Headroom is ' }));
    expect(screen.getByTestId('chat-draft').textContent).toBe('Headroom is ');

    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: '$1,800.' }));
    expect(screen.getByTestId('chat-draft').textContent).toBe('Headroom is $1,800.');
  });

  it('ignores a streamed chunk for a DIFFERENT conversation (another open thread/window)', async () => {
    loadedEmpty();
    sendMock.mockReturnValue(new Promise(() => {}));

    await renderThread('c1');
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'headroom?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    act(() => chatChunkHandler?.({ conversationId: 'OTHER', delta: 'not for this thread' }));

    expect(screen.queryByTestId('chat-draft')).toBeNull();
  });

  it('drops a chunk that arrives for this conversation AFTER a successful turn has already settled (gh#1085)', async () => {
    // The hub push is fire-and-forget and can be delivered late (slow connection, a reconnect replay window, or
    // simple jitter placing it after the terminal REST response) -- a straggler for a turn that already rendered
    // its settled answer must not resurrect a "the co-pilot is typing…" bubble under it.
    loadedEmpty();
    const turn = deferred<Awaited<ReturnType<typeof sendChatTurn>>>();
    sendMock.mockReturnValue(turn.promise);

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'headroom?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await act(async () => {
      turn.settle({
        ok: true,
        data: {
          userMessage: message({
            id: 'u1',
            sequence: 1,
            role: ChatRole.User,
            content: 'headroom?',
          }),
          assistantMessage: message({
            id: 'a1',
            sequence: 2,
            role: ChatRole.Assistant,
            content: 'Headroom is $1,800.',
          }),
        },
      });
    });
    expect(screen.queryByTestId('chat-draft')).toBeNull(); // sanity: settled cleanly, no draft standing

    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'a straggler token' }));

    expect(screen.queryByTestId('chat-draft')).toBeNull();
  });

  it('drops a chunk that arrives for this conversation AFTER a refused turn has already settled (gh#1085)', async () => {
    loadedEmpty();
    sendMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 429,
      reason: 'Daily AI budget reached; resets at 00:00 UTC.',
    });

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'one more?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByRole('alert'); // the turn has settled (refused) before the straggler arrives
    expect(screen.queryByTestId('chat-draft')).toBeNull(); // sanity: settled cleanly, no draft standing

    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'a straggler token' }));

    expect(screen.queryByTestId('chat-draft')).toBeNull();
  });

  it('streams the NEXT turn sent from here even if the last one was never terminated by its push', async () => {
    // The straggler suppression is armed by settling a turn whose push has not arrived; it must not survive into
    // the turn after it. Sending is itself proof the previous turn is over, so the send re-arms the stream --
    // otherwise a lost push would leave this connection permanently unable to render its own drafts.
    loadedEmpty();
    sendMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 429,
      reason: 'Daily AI budget reached; resets at 00:00 UTC.',
    });

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'one more?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    await screen.findByRole('alert'); // the first turn has settled, with no push behind it

    sendMock.mockReturnValue(new Promise(() => {})); // the second turn stays in flight
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'headroom?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'Headroom is ' }));

    expect(screen.getByTestId('chat-draft').textContent).toBe('Headroom is ');
  });

  it('still streams a later turn when the settled push BEAT the REST response (gh#1103)', async () => {
    // The straggler window closed by gh#1085 is bounded by the turn's settled message push: chunks and that push
    // travel the same connection in send order, so nothing of this turn can follow it. When it lands BEFORE the
    // REST response resolves -- the ordinary race, per the module note -- the turn is already terminated and the
    // settle must NOT leave this connection deaf to the next turn's chunks (one taken on another screen, which
    // never re-arms anything here).
    loadedEmpty();
    const turn = deferred<Awaited<ReturnType<typeof sendChatTurn>>>();
    sendMock.mockReturnValue(turn.promise);

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'headroom?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'Headroom is $1,8' }));

    act(() =>
      chatMessageHandler?.({
        conversationId: 'c1',
        messageId: 'a1',
        sequence: 2,
        role: ChatRole.Assistant,
        content: 'Headroom is $1,800.',
        at: '2026-08-01T00:02:00Z',
      }),
    );
    await act(async () => {
      turn.settle({
        ok: true,
        data: {
          userMessage: message({
            id: 'u1',
            sequence: 1,
            role: ChatRole.User,
            content: 'headroom?',
          }),
          assistantMessage: message({
            id: 'a1',
            sequence: 2,
            role: ChatRole.Assistant,
            content: 'Headroom is $1,800.',
          }),
        },
      });
    });
    expect(screen.queryByTestId('chat-draft')).toBeNull(); // sanity: settled cleanly, no draft standing

    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'Two contracts' }));

    expect(screen.getByTestId('chat-draft').textContent).toBe('Two contracts');
  });

  it('on success, replaces the optimistic turn and the streamed draft with the settled pair, and clears the composer', async () => {
    loadedEmpty();
    const turn = deferred<Awaited<ReturnType<typeof sendChatTurn>>>();
    sendMock.mockReturnValue(turn.promise);

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'headroom?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'Headroom is $1,800.' }));

    await act(async () => {
      turn.settle({
        ok: true,
        data: {
          userMessage: message({
            id: 'u1',
            sequence: 1,
            role: ChatRole.User,
            content: 'headroom?',
          }),
          assistantMessage: message({
            id: 'a1',
            sequence: 2,
            role: ChatRole.Assistant,
            content: 'Headroom is $1,800.',
          }),
        },
      });
    });

    expect(screen.queryByTestId('chat-draft')).toBeNull();
    const rows = screen.getAllByTestId('chat-message');
    expect(rows.map((row) => row.textContent)).toEqual(['headroom?', 'Headroom is $1,800.']);
    expect((screen.getByLabelText(/Message/i) as HTMLInputElement).value).toBe('');
    expect((screen.getByLabelText(/Message/i) as HTMLInputElement).disabled).toBe(false);
  });

  it('does not duplicate the assistant turn when the hub push wins the race against the REST response', async () => {
    // ChatEndpoints.TurnAsync pushes the assistant message over the owner-scoped hub -- to EVERY one of the
    // operator's connections, including the sender's own -- BEFORE returning the REST response. So on an ordinary
    // send, onChatMessage firing for the assistant's real id before sendChatTurn's promise settles is the near-
    // guaranteed order, not a rare multi-tab edge case. The success branch must reconcile by id the same way the
    // onChatMessage handler itself does, or the same turn renders twice under colliding React keys.
    loadedEmpty();
    const turn = deferred<Awaited<ReturnType<typeof sendChatTurn>>>();
    sendMock.mockReturnValue(turn.promise);

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'headroom?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    // The hub delivers the assistant's turn FIRST, on this same connection.
    act(() =>
      chatMessageHandler?.({
        conversationId: 'c1',
        messageId: 'a1',
        sequence: 2,
        role: ChatRole.Assistant,
        content: 'Headroom is $1,800.',
        at: '2026-08-01T00:02:00Z',
      }),
    );
    expect(screen.getAllByTestId('chat-message').map((row) => row.textContent)).toEqual([
      'headroom?', // still the optimistic bubble -- the REST response has not settled yet
      'Headroom is $1,800.',
    ]);

    // THEN the REST call this same send started resolves with the identical pair.
    await act(async () => {
      turn.settle({
        ok: true,
        data: {
          userMessage: message({
            id: 'u1',
            sequence: 1,
            role: ChatRole.User,
            content: 'headroom?',
          }),
          assistantMessage: message({
            id: 'a1',
            sequence: 2,
            role: ChatRole.Assistant,
            content: 'Headroom is $1,800.',
          }),
        },
      });
    });

    const rows = screen.getAllByTestId('chat-message');
    expect(rows.map((row) => row.textContent)).toEqual(['headroom?', 'Headroom is $1,800.']);
    expect(rows).toHaveLength(2); // exactly one assistant row, not a duplicate under a colliding key
  });

  it('on a refused turn, drops the optimistic bubble, keeps the typed text for retry, and shows the reason', async () => {
    loadedEmpty();
    sendMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 429,
      reason: 'Daily AI budget reached; resets at 00:00 UTC.',
    });

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'one more?' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByRole('alert')).toHaveProperty(
      'textContent',
      'Daily AI budget reached; resets at 00:00 UTC.',
    );
    expect(screen.queryByText('one more?')).toBeNull(); // the optimistic bubble is gone -- it never persisted
    expect((screen.getByLabelText(/Message/i) as HTMLInputElement).value).toBe('one more?'); // typed text survives
    expect((screen.getByLabelText(/Message/i) as HTMLInputElement).disabled).toBe(false);
  });

  it('a THROW out of the send re-enables the composer rather than stranding it busy (gh#973 class)', async () => {
    loadedEmpty();
    sendMock.mockRejectedValue(new Error('boom'));

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: 'hello' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect((screen.getByLabelText(/Message/i) as HTMLInputElement).disabled).toBe(false);
  });

  it('does not send a blank message', async () => {
    loadedEmpty();

    await renderThread();
    fireEvent.change(screen.getByLabelText(/Message/i), { target: { value: '   ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(sendMock).not.toHaveBeenCalled();
  });
});

describe('ConversationThread — cross-connection reconciliation', () => {
  it('folds in a message pushed for this conversation from another connection, without duplicating it', async () => {
    // A pop-out window / another tab took a turn; the primary read model is the REST response there, but THIS
    // connection should still reconcile so the same turn appears here too (ADR-0006/ADR-0021).
    getMock.mockResolvedValue({
      ok: true,
      data: {
        id: 'c1',
        title: null,
        createdAt: '',
        updatedAt: '',
        messages: [
          message({ id: 'm1', sequence: 1, role: ChatRole.User, content: 'from elsewhere' }),
        ],
      },
    });

    await renderThread();
    act(() =>
      chatMessageHandler?.({
        conversationId: 'c1',
        messageId: 'm2',
        sequence: 2,
        role: ChatRole.Assistant,
        content: 'answered on the other screen',
        at: '2026-08-01T00:02:00Z',
      }),
    );

    expect(screen.getByText('answered on the other screen')).toBeTruthy();
    expect(screen.getAllByTestId('chat-message')).toHaveLength(2);

    // The identical push arriving twice (e.g. a redundant hub delivery) must not duplicate the row.
    act(() =>
      chatMessageHandler?.({
        conversationId: 'c1',
        messageId: 'm2',
        sequence: 2,
        role: ChatRole.Assistant,
        content: 'answered on the other screen',
        at: '2026-08-01T00:02:00Z',
      }),
    );
    expect(screen.getAllByTestId('chat-message')).toHaveLength(2);
  });

  it('ignores a cross-connection push for a DIFFERENT conversation', async () => {
    getMock.mockResolvedValue({
      ok: true,
      data: { id: 'c1', title: null, createdAt: '', updatedAt: '', messages: [] },
    });

    await renderThread('c1');
    act(() =>
      chatMessageHandler?.({
        conversationId: 'OTHER',
        messageId: 'm9',
        sequence: 1,
        role: ChatRole.Assistant,
        content: 'not for this thread',
        at: '',
      }),
    );

    expect(screen.queryByText('not for this thread')).toBeNull();
  });

  // The chunk half of the same contract (gh#1103). `realtimeChatChunk` is pushed per-OWNER -- to every connection
  // the operator has open -- and ADR-0021 states the push exists to serve the owner's OTHER connections. So a
  // connection that never called send() is the audience for the live draft, not an eavesdropper on one.
  function loadedEmptyThread() {
    getMock.mockResolvedValue({
      ok: true,
      data: { id: 'c1', title: null, createdAt: '', updatedAt: '', messages: [] },
    });
  }

  const settledAssistantPush = {
    conversationId: 'c1',
    messageId: 'a1',
    sequence: 2,
    role: ChatRole.Assistant,
    content: 'Headroom is $1,800.',
    at: '2026-08-01T00:02:00Z',
  };

  it('renders the live streaming draft of a turn taken on ANOTHER connection (gh#1103)', async () => {
    loadedEmptyThread();

    await renderThread(); // this connection never sends -- it is the other screen watching

    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'Headroom is ' }));
    expect(screen.getByTestId('chat-draft').textContent).toBe('Headroom is ');

    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: '$1,8' }));
    expect(screen.getByTestId('chat-draft').textContent).toBe('Headroom is $1,8');
  });

  it('ignores a streamed chunk for a DIFFERENT conversation on a connection that never sent', async () => {
    loadedEmptyThread();

    await renderThread('c1');
    act(() => chatChunkHandler?.({ conversationId: 'OTHER', delta: 'not for this thread' }));

    expect(screen.queryByTestId('chat-draft')).toBeNull();
  });

  it('swaps the passive draft for the settled message, leaving no stale duplicate (gh#1103)', async () => {
    // The pre-gh#1085 bug this closes rather than relocates: `streaming` was cleared only inside send()'s own
    // settle path, so a connection that did not send had its draft left standing under the real answer forever.
    loadedEmptyThread();

    await renderThread();
    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'Headroom is $1,8' }));
    expect(screen.getByTestId('chat-draft')).toBeTruthy();

    act(() => chatMessageHandler?.(settledAssistantPush));

    expect(screen.queryByTestId('chat-draft')).toBeNull();
    const rows = screen.getAllByTestId('chat-message');
    expect(rows.map((row) => row.textContent)).toEqual(['Headroom is $1,800.']);
  });

  it('keeps a live draft standing when the push that arrives is the operator turn, not the answer', async () => {
    // Only the ASSISTANT message terminates a turn's chunk stream. A user-role push (the operator's own turn,
    // taken on the other screen) precedes the answer being generated -- clearing the draft on it would blank the
    // very stream it introduces.
    loadedEmptyThread();

    await renderThread();
    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'Headroom is ' }));
    act(() =>
      chatMessageHandler?.({
        conversationId: 'c1',
        messageId: 'u1',
        sequence: 1,
        role: ChatRole.User,
        content: 'headroom?',
        at: '2026-08-01T00:01:00Z',
      }),
    );

    expect(screen.getByTestId('chat-draft').textContent).toBe('Headroom is ');
  });

  it('streams a LATER cross-connection turn too -- settling one turn is not a latch (gh#1103)', async () => {
    loadedEmptyThread();

    await renderThread();
    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'Headroom is $1,8' }));
    act(() => chatMessageHandler?.(settledAssistantPush));
    expect(screen.queryByTestId('chat-draft')).toBeNull();

    act(() => chatChunkHandler?.({ conversationId: 'c1', delta: 'Two contracts' }));

    expect(screen.getByTestId('chat-draft').textContent).toBe('Two contracts');
  });
});
