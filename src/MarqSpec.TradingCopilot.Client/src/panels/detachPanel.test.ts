import { afterEach, describe, expect, it, vi } from 'vitest';

import { detachPanel, panelRoute } from './detachPanel';

describe('panelRoute', () => {
  it('addresses a panel by its id — a bookmarkable, reopenable route (ADR-0006)', () => {
    expect(panelRoute('blotter')).toBe('/panel/blotter');
  });
});

describe('detachPanel', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('opens the panel route in a deterministically-named pop-out window, then focuses it', () => {
    const handle = { focus: vi.fn() } as unknown as Window;
    const open = vi.spyOn(window, 'open').mockReturnValue(handle);

    const result = detachPanel('blotter');

    // The name is deterministic per panel so re-detaching FOCUSES the window it already opened rather than
    // stacking duplicates -- the browser reuses a window of the same name.
    expect(open).toHaveBeenCalledWith(
      '/panel/blotter',
      'copilot-panel-blotter',
      expect.stringContaining('popup'),
    );
    expect(handle.focus).toHaveBeenCalled();
    expect(result).toBe(handle);
  });

  it('returns null without throwing when the browser blocks the pop-up', () => {
    // A blocked pop-up hands back null; the caller stays on its feet rather than dereferencing it.
    vi.spyOn(window, 'open').mockReturnValue(null);

    expect(detachPanel('suggestions')).toBeNull();
  });
});
