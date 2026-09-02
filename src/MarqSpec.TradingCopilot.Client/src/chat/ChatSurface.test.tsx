import { act, cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { type Conversation, createConversation, getConversation, listConversations } from '../api/chat';
import { renderWithProviders } from '../testing/render';
import { ChatSurface } from './ChatSurface';

vi.mock('../api/chat', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/chat')>()),
  listConversations: vi.fn(),
  createConversation: vi.fn(),
  getConversation: vi.fn(),
  sendChatTurn: vi.fn(),
}));

const { useRealtimeMock } = vi.hoisted(() => ({ useRealtimeMock: vi.fn() }));
vi.mock('../realtime/RealtimeProvider', () => ({ useRealtime: useRealtimeMock }));

const listMock = vi.mocked(listConversations);
const createMock = vi.mocked(createConversation);
const getMock = vi.mocked(getConversation);

function conversation(overrides: Partial<Conversation> = {}): Conversation {
  return {
    id: 'c1',
    title: 'ES setups',
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:05:00Z',
    ...overrides,
  };
}

const DESTINATION = {
  id: 'chat',
  path: '/chat',
  label: 'Chat',
  summary: '',
  requirement: 'R-6',
  Icon: (() => null) as never,
  tier: 'primary' as const,
};

async function renderSurface() {
  const view = renderWithProviders(<ChatSurface destination={DESTINATION} />);
  await act(async () => {});
  return view;
}

beforeEach(() => {
  vi.clearAllMocks();
  useRealtimeMock.mockReturnValue({
    connectionState: 'live',
    onEvent: vi.fn(() => vi.fn()),
    onOrderState: vi.fn(() => vi.fn()),
    onFill: vi.fn(() => vi.fn()),
    onSuggestion: vi.fn(() => vi.fn()),
    onResync: vi.fn(() => vi.fn()),
    onChatChunk: vi.fn(() => vi.fn()),
    onChatMessage: vi.fn(() => vi.fn()),
  });
  getMock.mockResolvedValue({
    ok: true,
    data: { id: 'c1', title: 'ES setups', createdAt: '', updatedAt: '', messages: [] },
  });
});

afterEach(cleanup);

describe('ChatSurface', () => {
  it('carries the shell surface contract', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });

    await renderSurface();

    const surface = screen.getByTestId('surface');
    expect(surface.getAttribute('data-surface')).toBe('chat');
  });

  it('loads the conversation list and auto-selects the most recent one', async () => {
    listMock.mockResolvedValue({
      ok: true,
      data: [conversation({ id: 'c1', title: 'ES setups' }), conversation({ id: 'c2', title: 'Journal review' })],
    });

    await renderSurface();

    expect(getMock).toHaveBeenCalledWith('c1');
    expect(screen.getByTestId('conversation-thread')).toBeTruthy();
  });

  it('shows a prompt to start a conversation when there are none, with no thread mounted', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });

    await renderSurface();

    expect(screen.queryByTestId('conversation-thread')).toBeNull();
    // Both panes say so: the list's own empty state, and the thread pane's "start a conversation" prompt.
    expect(screen.getAllByTestId('empty-state')).toHaveLength(2);
    expect(screen.getByText('Start a conversation')).toBeTruthy();
  });

  it('selecting a different conversation swaps the thread', async () => {
    listMock.mockResolvedValue({
      ok: true,
      data: [conversation({ id: 'c1', title: 'ES setups' }), conversation({ id: 'c2', title: 'Journal review' })],
    });
    getMock.mockImplementation((id) =>
      Promise.resolve({
        ok: true,
        data: { id, title: id === 'c1' ? 'ES setups' : 'Journal review', createdAt: '', updatedAt: '', messages: [] },
      }),
    );

    await renderSurface();
    expect(getMock).toHaveBeenCalledWith('c1');

    fireEvent.click(screen.getByText('Journal review'));
    await act(async () => {});

    expect(getMock).toHaveBeenCalledWith('c2');
  });

  it('creating a conversation selects it immediately and prepends it to the list', async () => {
    listMock.mockResolvedValue({ ok: true, data: [conversation({ id: 'c1', title: 'ES setups' })] });
    createMock.mockResolvedValue({ ok: true, data: conversation({ id: 'c2', title: null }) });

    await renderSurface();
    fireEvent.click(screen.getByRole('button', { name: /New conversation/i }));
    await act(async () => {});

    expect(createMock).toHaveBeenCalledOnce();
    expect(getMock).toHaveBeenLastCalledWith('c2');
    const rows = screen.getAllByTestId('conversation-row');
    expect(rows[0].textContent).toBe('Untitled'); // the new (unselected-until-now) conversation leads
  });

  it('a failed create surfaces an error without disturbing the current selection', async () => {
    listMock.mockResolvedValue({ ok: true, data: [conversation({ id: 'c1', title: 'ES setups' })] });
    createMock.mockResolvedValue({ ok: false, kind: 'failed', error: 'The request could not be sent.' });

    await renderSurface();
    fireEvent.click(screen.getByRole('button', { name: /New conversation/i }));
    await act(async () => {});

    expect(await screen.findByRole('alert')).toHaveProperty(
      'textContent',
      'The request could not be sent.',
    );
    expect(getMock).toHaveBeenCalledWith('c1');
    expect(getMock).not.toHaveBeenCalledWith('c2');
  });

  it('a failed list load renders an error with retry', async () => {
    listMock.mockResolvedValue({ ok: false, kind: 'failed', error: 'The request could not be sent.' });

    await renderSurface();

    expect(await screen.findByTestId('conversations-error')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    listMock.mockResolvedValue({ ok: true, data: [] });
    await act(async () => {});

    expect(screen.getByTestId('empty-state')).toBeTruthy();
  });
});
