import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { detachPanel } from './detachPanel';
import { loadDetachedPanels, saveDetachedPanels } from './panelLayout';
import { usePanelLayout } from './usePanelLayout';

vi.mock('./detachPanel', () => ({ detachPanel: vi.fn() }));
const detach = vi.mocked(detachPanel);

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.clearAllMocks();
});

describe('usePanelLayout', () => {
  it('detaches a panel — opens the window, marks it detached, and persists for the next reload', () => {
    detach.mockReturnValue({ close: vi.fn() } as unknown as Window);
    const { result } = renderHook(() => usePanelLayout());

    act(() => result.current.detach('blotter'));

    expect(detach).toHaveBeenCalledWith('blotter');
    expect(result.current.isDetached('blotter')).toBe(true);
    expect(loadDetachedPanels().has('blotter')).toBe(true);
  });

  it('reattaches — closes the window it opened this session, re-docks, and persists', () => {
    const close = vi.fn();
    detach.mockReturnValue({ close } as unknown as Window);
    const { result } = renderHook(() => usePanelLayout());
    act(() => result.current.detach('blotter'));

    act(() => result.current.reattach('blotter'));

    expect(close).toHaveBeenCalled();
    expect(result.current.isDetached('blotter')).toBe(false);
    expect(loadDetachedPanels().has('blotter')).toBe(false);
  });

  it('stays docked when the browser blocks the pop-up, so the panel is never stranded on a phantom reattach', () => {
    detach.mockReturnValue(null); // blocked pop-up
    const { result } = renderHook(() => usePanelLayout());

    act(() => result.current.detach('blotter'));

    expect(result.current.isDetached('blotter')).toBe(false);
    expect(loadDetachedPanels().has('blotter')).toBe(false);
  });

  it('restores the persisted layout on mount, so a reload reflects the last pop-out set', () => {
    saveDetachedPanels(new Set(['suggestions']));

    const { result } = renderHook(() => usePanelLayout());

    expect(result.current.isDetached('suggestions')).toBe(true);
  });

  it('re-docks without throwing when it never held the window (a pop-out that outlived a reload)', () => {
    // The cross-window limit ADR-0006 leaves to a later increment: this tab has no handle to a window a prior
    // session opened, so reattach re-docks the layout but cannot itself close the orphaned window. It must not throw.
    saveDetachedPanels(new Set(['blotter']));
    const { result } = renderHook(() => usePanelLayout());

    act(() => result.current.reattach('blotter'));

    expect(result.current.isDetached('blotter')).toBe(false);
  });
});
