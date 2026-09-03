import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  ChatRole,
  type ChatTurnResult,
  type Conversation,
  type ConversationDetail,
  createConversation,
  getConversation,
  listConversations,
  sendChatTurn,
} from './chat';

function response(status: number, body?: unknown): Response {
  const text = body === undefined ? '' : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    text: () => Promise.resolve(text),
  } as unknown as Response;
}

function stubFetch(impl: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>) {
  const mock = vi.fn(impl);
  vi.stubGlobal('fetch', mock);
  return mock;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const CONVERSATION: Conversation = {
  id: 'c1',
  title: 'ES setups',
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-01T00:05:00Z',
};

describe('createConversation', () => {
  it('POSTs to /conversations with the given title', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, CONVERSATION)));

    const result = await createConversation('ES setups');

    expect(mock.mock.calls[0][0]).toBe('/conversations');
    expect(JSON.parse(String(mock.mock.calls[0][1]?.body))).toEqual({ title: 'ES setups' });
    expect(result).toEqual({ ok: true, data: CONVERSATION });
  });

  it('sends a null title when none is given -- a conversation may start untitled', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, { ...CONVERSATION, title: null })));

    await createConversation();

    expect(JSON.parse(String(mock.mock.calls[0][1]?.body))).toEqual({ title: null });
  });

  it('surfaces an over-length title as a refusal, not a fabricated conversation', async () => {
    stubFetch(() =>
      Promise.resolve(response(400, { error: 'Title must be at most 256 characters.' })),
    );

    const result = await createConversation('x'.repeat(300));

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.kind).toBe('refused');
  });
});

describe('listConversations', () => {
  it('reads /conversations and unwraps the envelope to the conversation array', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, { conversations: [CONVERSATION] })));

    const result = await listConversations();

    expect(mock.mock.calls[0][0]).toBe('/conversations');
    expect(result).toEqual({ ok: true, data: [CONVERSATION] });
  });

  it('passes a limit as a query param when given', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, { conversations: [] })));

    await listConversations(10);

    expect(mock.mock.calls[0][0]).toBe('/conversations?limit=10');
  });

  it('surfaces a 5xx as failed -- never a fabricated empty list', async () => {
    stubFetch(() => Promise.resolve(response(500)));

    const result = await listConversations();

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.kind).toBe('failed');
  });
});

const DETAIL: ConversationDetail = {
  ...CONVERSATION,
  messages: [
    {
      id: 'm1',
      conversationId: 'c1',
      sequence: 1,
      role: ChatRole.User,
      content: "What's my risk if I take it?",
      createdAt: '2026-08-01T00:01:00Z',
    },
    {
      id: 'm2',
      conversationId: 'c1',
      sequence: 2,
      role: ChatRole.Assistant,
      content: '-$412 to the 5222 stop.',
      createdAt: '2026-08-01T00:01:05Z',
    },
  ],
};

describe('getConversation', () => {
  it('reads /conversations/{id} with its messages in order', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, DETAIL)));

    const result = await getConversation('c1');

    expect(mock.mock.calls[0][0]).toBe('/conversations/c1');
    expect(result).toEqual({ ok: true, data: DETAIL });
  });

  it('a 404 (absent or foreign conversation, R-20) surfaces as a refusal', async () => {
    stubFetch(() => Promise.resolve(response(404)));

    const result = await getConversation('not-mine');

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.status).toBe(404);
  });
});

const TURN_RESULT: ChatTurnResult = {
  userMessage: DETAIL.messages[0],
  assistantMessage: DETAIL.messages[1],
};

describe('sendChatTurn', () => {
  it('POSTs the operator message to /conversations/{id}/turns', async () => {
    const mock = stubFetch(() => Promise.resolve(response(200, TURN_RESULT)));

    const result = await sendChatTurn('c1', "What's my risk if I take it?");

    expect(mock.mock.calls[0][0]).toBe('/conversations/c1/turns');
    expect(JSON.parse(String(mock.mock.calls[0][1]?.body))).toEqual({
      content: "What's my risk if I take it?",
    });
    expect(result).toEqual({ ok: true, data: TURN_RESULT });
  });

  it('a governor-blocked turn (429) surfaces as a refusal with the reason -- never an invented reply', async () => {
    stubFetch(() =>
      Promise.resolve(response(429, { error: 'Daily AI budget reached; resets at 00:00 UTC.' })),
    );

    const result = await sendChatTurn('c1', 'one more');

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.kind === 'refused' && result.reason).toBe(
      'Daily AI budget reached; resets at 00:00 UTC.',
    );
  });

  it('a faulted/refused turn (422) surfaces as a refusal -- the user turn is still saved server-side', async () => {
    stubFetch(() => Promise.resolve(response(422, { error: 'The model call failed.' })));

    const result = await sendChatTurn('c1', 'hello');

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.kind).toBe('refused');
  });
});
