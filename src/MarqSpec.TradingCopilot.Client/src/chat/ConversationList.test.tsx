import { cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { Conversation } from '../api/chat';
import { renderWithProviders } from '../testing/render';
import { ConversationList } from './ConversationList';

afterEach(cleanup);

function conversation(overrides: Partial<Conversation> = {}): Conversation {
  return {
    id: 'c1',
    title: 'ES setups',
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:05:00Z',
    ...overrides,
  };
}

describe('ConversationList', () => {
  it('renders each conversation title, most-recent-first order as given -- it does not re-sort', () => {
    render1([
      conversation({ id: 'c1', title: 'ES setups' }),
      conversation({ id: 'c2', title: 'Journal review' }),
    ]);

    const rows = screen.getAllByTestId('conversation-row');
    expect(rows.map((row) => row.textContent)).toEqual(['ES setups', 'Journal review']);
  });

  it('falls back to "Untitled" for a null title, rather than a blank row', () => {
    render1([conversation({ id: 'c1', title: null })]);

    expect(screen.getByText('Untitled')).toBeTruthy();
  });

  it('marks the selected conversation and calls onSelect with its id when another is clicked', () => {
    const onSelect = vi.fn();
    renderWithProviders(
      <ConversationList
        conversations={[
          conversation({ id: 'c1', title: 'ES setups' }),
          conversation({ id: 'c2', title: 'Journal review' }),
        ]}
        selectedId="c1"
        onSelect={onSelect}
        onNew={vi.fn()}
        creating={false}
      />,
    );

    const rows = screen.getAllByTestId('conversation-row');
    expect(rows[0].getAttribute('aria-current')).toBe('true');
    expect(rows[1].getAttribute('aria-current')).toBe('false');

    fireEvent.click(screen.getByText('Journal review'));
    expect(onSelect).toHaveBeenCalledWith('c2');
  });

  it('calls onNew when "New conversation" is pressed, and disables it while creating', () => {
    const onNew = vi.fn();
    renderWithProviders(
      <ConversationList
        conversations={[]}
        selectedId={null}
        onSelect={vi.fn()}
        onNew={onNew}
        creating={false}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /New conversation/i }));
    expect(onNew).toHaveBeenCalledOnce();
  });

  it('disables "New conversation" while a create is in flight', () => {
    renderWithProviders(
      <ConversationList
        conversations={[]}
        selectedId={null}
        onSelect={vi.fn()}
        onNew={vi.fn()}
        creating
      />,
    );

    expect(
      (screen.getByRole('button', { name: /New conversation/i }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  it('shows an empty-state prompt when there are no conversations yet', () => {
    render1([]);

    expect(screen.getByTestId('empty-state')).toBeTruthy();
  });
});

function render1(conversations: readonly Conversation[]) {
  return renderWithProviders(
    <ConversationList
      conversations={conversations}
      selectedId={conversations[0]?.id ?? null}
      onSelect={vi.fn()}
      onNew={vi.fn()}
      creating={false}
    />,
  );
}
