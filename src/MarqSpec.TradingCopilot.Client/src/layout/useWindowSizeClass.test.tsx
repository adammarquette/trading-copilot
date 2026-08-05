import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { windowSizeClassMinWidth } from '../theme/tokens';
import { setViewportWidth } from '../testing/viewport';
import { useWindowSizeClass } from './useWindowSizeClass';

function SizeClassProbe() {
  return <span data-testid="size-class">{useWindowSizeClass()}</span>;
}

function sizeClassAt(width: number): string {
  setViewportWidth(width);
  render(<SizeClassProbe />);
  const value = screen.getByTestId('size-class').textContent ?? '';
  cleanup();
  return value;
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('useWindowSizeClass', () => {
  it('uses the Material 3 window size class thresholds ADR-0005 specifies', () => {
    // Pinned as numbers, not just as behaviour. The wireframe switches at 720/900px -- illustrative
    // numbers chosen so three device frames fit one page -- and it would be easy to copy those across
    // and land the layout switch in the wrong place.
    expect(windowSizeClassMinWidth).toStrictEqual({ compact: 0, medium: 600, expanded: 840 });
  });

  it.each([
    [320, 'compact'],
    [599, 'compact'],
    [600, 'medium'],
    [839, 'medium'],
    [840, 'expanded'],
    [1920, 'expanded'],
  ])('reports %ipx as %s', (width, expected) => {
    // The boundaries themselves, because off-by-one at a breakpoint is the bug that only ever shows up
    // on someone's actual tablet.
    expect(sizeClassAt(width)).toBe(expected);
  });
});
