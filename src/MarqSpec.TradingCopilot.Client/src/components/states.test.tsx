import { cleanup, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { renderWithProviders } from '../testing/render';
import { EmptyState } from './EmptyState';
import { LoadingState } from './LoadingState';

afterEach(cleanup);

describe('LoadingState', () => {
  it('announces what it is waiting on', () => {
    // A bare spinner tells the operator something is happening but not what. In a cockpit with several
    // panes loading at once, that is close to no information at all.
    renderWithProviders(<LoadingState label="Loading positions" />);

    const status = screen.getByRole('status');

    expect(status.textContent).toContain('Loading positions');
    expect(status.getAttribute('aria-live')).toBe('polite');
  });
});

describe('EmptyState', () => {
  it('states what is empty', () => {
    renderWithProviders(<EmptyState title="No fills today" />);

    expect(screen.getByRole('heading', { level: 2 }).textContent).toBe('No fills today');
  });

  it('is distinguishable from a loading state', () => {
    // The convention exists so "nothing here", "still loading" and "this failed" never look alike. If
    // the empty state also claimed role="status", a test -- and a screen reader -- could not tell them
    // apart.
    renderWithProviders(<EmptyState title="No fills today" />);

    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.getByTestId('empty-state')).toBeTruthy();
  });

  it('carries an optional description, action and requirement tag', () => {
    renderWithProviders(
      <EmptyState
        title="Journal"
        description="Realized P&L by day."
        tag="R-8"
        action={<button type="button">Add an entry</button>}
      />,
    );

    expect(screen.getByText('Realized P&L by day.')).toBeTruthy();
    expect(screen.getByText('R-8')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Add an entry' })).toBeTruthy();
  });
});
