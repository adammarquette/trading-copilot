import { cleanup, fireEvent, screen } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { renderWithProviders } from '../testing/render';
import { ErrorBoundary } from './ErrorBoundary';

function Boom({ explode }: { readonly explode: boolean }) {
  if (explode) throw new Error('the pane exploded');
  return <p>Recovered content</p>;
}

/** Lets a test make the child stop throwing before pressing "Try again", as a real fix would. */
function RepairableSubtree() {
  const [explode, setExplode] = useState(true);

  return (
    <>
      <button type="button" onClick={() => setExplode(false)}>
        Repair
      </button>
      <ErrorBoundary>
        <Boom explode={explode} />
      </ErrorBoundary>
    </>
  );
}

function silenceReactErrorLogging() {
  // React logs every caught error to the console. These tests throw on purpose, so the log is noise --
  // the assertions are what prove the boundary worked.
  vi.spyOn(console, 'error').mockImplementation(() => {});
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('ErrorBoundary', () => {
  it('renders its children when nothing throws', () => {
    renderWithProviders(
      <ErrorBoundary>
        <Boom explode={false} />
      </ErrorBoundary>,
    );

    expect(screen.getByText('Recovered content')).toBeTruthy();
    expect(screen.queryByTestId('error-boundary-fallback')).toBeNull();
  });

  it('catches a throwing child and shows the failure instead of a blank page', () => {
    silenceReactErrorLogging();

    renderWithProviders(
      <ErrorBoundary>
        <Boom explode />
      </ErrorBoundary>,
    );

    expect(screen.getByTestId('error-boundary-fallback')).toBeTruthy();
    // The operator gets something quotable. Without a boundary React unmounts the whole tree, and a
    // white page during a live position is the worst outcome this shell can produce.
    expect(screen.getByText('the pane exploded')).toBeTruthy();
  });

  it('offers a recovery that actually re-renders the subtree', () => {
    // "Recoverable" has to mean the retry re-mounts the children, not that it hides the message. The
    // child is repaired first, so a retry that merely cleared state would loop straight back to the
    // error and this test would still catch a fake recovery.
    silenceReactErrorLogging();

    renderWithProviders(<RepairableSubtree />);
    expect(screen.getByTestId('error-boundary-fallback')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Repair' }));
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(screen.queryByTestId('error-boundary-fallback')).toBeNull();
    expect(screen.getByText('Recovered content')).toBeTruthy();
  });

  it('clears itself when the reset key moves', () => {
    // The shell passes the router's location key, so navigating away from a broken surface does not
    // strand the operator on its error card.
    silenceReactErrorLogging();

    const { rerender } = renderWithProviders(
      <ErrorBoundary resetKey="surface-a">
        <Boom explode />
      </ErrorBoundary>,
    );
    expect(screen.getByTestId('error-boundary-fallback')).toBeTruthy();

    rerender(
      <ErrorBoundary resetKey="surface-b">
        <Boom explode={false} />
      </ErrorBoundary>,
    );

    expect(screen.queryByTestId('error-boundary-fallback')).toBeNull();
    expect(screen.getByText('Recovered content')).toBeTruthy();
  });

  it('reports the error to the caller instead of only the console', () => {
    silenceReactErrorLogging();
    const onError = vi.fn();

    renderWithProviders(
      <ErrorBoundary onError={onError}>
        <Boom explode />
      </ErrorBoundary>,
    );

    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError.mock.calls[0][0]).toBeInstanceOf(Error);
    expect((onError.mock.calls[0][0] as Error).message).toBe('the pane exploded');
  });

  it('renders a message even when the thrown value is not an Error', () => {
    // Libraries throw strings and plain objects. Normalising means the failure card never has to show
    // "[object Object]" to someone trying to describe what happened.
    silenceReactErrorLogging();

    function ThrowsAString(): never {
      throw 'a bare string';
    }

    renderWithProviders(
      <ErrorBoundary>
        <ThrowsAString />
      </ErrorBoundary>,
    );

    expect(screen.getByText('a bare string')).toBeTruthy();
  });
});
