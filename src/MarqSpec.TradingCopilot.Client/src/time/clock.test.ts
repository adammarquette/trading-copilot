import { act, cleanup, render } from '@testing-library/react';
import { createElement, type ReactElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { TICK_MS, useNow } from './clock';

/** A probe that renders the clock reading, so the DOM shows what a subscriber currently sees. */
function Probe(): ReactElement {
  return createElement('span', { 'data-testid': 'now' }, String(useNow()));
}

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe('useNow', () => {
  it('advances with the wall clock', () => {
    vi.setSystemTime(1_000_000);
    const { getByTestId } = render(createElement(Probe));
    expect(getByTestId('now').textContent).toBe('1000000');

    act(() => {
      vi.advanceTimersByTime(TICK_MS);
    });

    expect(getByTestId('now').textContent).toBe(String(1_000_000 + TICK_MS));
  });

  it('drives every subscriber off ONE interval, so two countdowns cannot disagree', () => {
    // The bug this prevents: independent intervals drift, and two cards with the same deadline then show
    // different seconds remaining and die at different moments.
    vi.setSystemTime(2_000_000);
    const timers = vi.getTimerCount();

    const { getAllByTestId } = render(
      createElement('div', null, createElement(Probe), createElement(Probe), createElement(Probe)),
    );

    expect(vi.getTimerCount()).toBe(timers + 1);

    act(() => {
      vi.advanceTimersByTime(TICK_MS);
    });

    const readings = getAllByTestId('now').map((node) => node.textContent);
    expect(new Set(readings).size).toBe(1);
  });

  it('runs no timer once the last subscriber unmounts', () => {
    // An idle app must not keep waking the tab.
    const before = vi.getTimerCount();
    const view = render(createElement(Probe));
    expect(vi.getTimerCount()).toBe(before + 1);

    view.unmount();

    expect(vi.getTimerCount()).toBe(before);
  });

  it('re-reads the clock when it restarts after an idle gap', () => {
    // The cached snapshot is arbitrarily stale if nothing has been counting down; a restart must not replay it.
    vi.setSystemTime(3_000_000);
    render(createElement(Probe)).unmount();

    vi.setSystemTime(9_000_000);
    const { getByTestId } = render(createElement(Probe));

    expect(getByTestId('now').textContent).toBe('9000000');
  });
});
