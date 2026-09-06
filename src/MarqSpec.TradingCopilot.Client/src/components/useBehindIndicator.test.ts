import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { useBehindIndicator } from './useBehindIndicator';

afterEach(cleanup);

describe('useBehindIndicator', () => {
  it('starts current -- not behind', () => {
    const { result } = renderHook(() => useBehindIndicator());

    expect(result.current.behind).toBe(false);
  });

  it('flags behind once a background refresh fails', () => {
    const { result } = renderHook(() => useBehindIndicator());

    act(() => result.current.markBehind());

    expect(result.current.behind).toBe(true);
  });

  it('clears the flag once a background refresh succeeds again', () => {
    const { result } = renderHook(() => useBehindIndicator());
    act(() => result.current.markBehind());

    act(() => result.current.clearBehind());

    expect(result.current.behind).toBe(false);
  });

  it('markBehind is idempotent -- a second failed refresh does not toggle anything off', () => {
    const { result } = renderHook(() => useBehindIndicator());

    act(() => result.current.markBehind());
    act(() => result.current.markBehind());

    expect(result.current.behind).toBe(true);
  });

  it('returns stable callback identities across renders, so a caller can subscribe them once', () => {
    const { result, rerender } = renderHook(() => useBehindIndicator());
    const firstMark = result.current.markBehind;
    const firstClear = result.current.clearBehind;

    rerender();

    expect(result.current.markBehind).toBe(firstMark);
    expect(result.current.clearBehind).toBe(firstClear);
  });
});
