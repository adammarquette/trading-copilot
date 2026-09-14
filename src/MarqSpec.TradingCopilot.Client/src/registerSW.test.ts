import { afterEach, describe, expect, it, vi } from 'vitest';

// Mock the vite-plugin-pwa virtual module before importing the unit under test. `vi.hoisted` lets the factory
// reference the spy, and the spy is what we assert the wrapper drives.
const { registerSWSpy } = vi.hoisted(() => ({ registerSWSpy: vi.fn() }));
vi.mock('virtual:pwa-register', () => ({ registerSW: registerSWSpy }));

import { registerAppShellServiceWorker } from './registerSW';

afterEach(() => {
  registerSWSpy.mockReset();
  vi.unstubAllGlobals();
});

describe('registerAppShellServiceWorker', () => {
  it('registers the worker where the browser supports service workers', () => {
    vi.stubGlobal('navigator', { serviceWorker: {} });
    registerSWSpy.mockReturnValue(vi.fn());

    registerAppShellServiceWorker();

    expect(registerSWSpy).toHaveBeenCalledTimes(1);
  });

  it('does nothing where service workers are unsupported, rather than throwing', () => {
    vi.stubGlobal('navigator', {});

    expect(() => registerAppShellServiceWorker()).not.toThrow();
    expect(registerSWSpy).not.toHaveBeenCalled();
  });

  it('surfaces a waiting shell through onNeedRefresh with a reload the caller can invoke', () => {
    vi.stubGlobal('navigator', { serviceWorker: {} });
    const update = vi.fn();
    let pluginOnNeedRefresh: (() => void) | undefined;
    registerSWSpy.mockImplementation((options: { onNeedRefresh?: () => void }) => {
      pluginOnNeedRefresh = options.onNeedRefresh;
      return update;
    });
    const onNeedRefresh = vi.fn();

    registerAppShellServiceWorker({ onNeedRefresh });
    // The plugin reports a new shell is waiting…
    pluginOnNeedRefresh?.();

    expect(onNeedRefresh).toHaveBeenCalledTimes(1);
    // …and the reload handed to the caller performs the swap only when invoked (never automatically).
    const reload = onNeedRefresh.mock.calls[0][0] as () => void;
    expect(update).not.toHaveBeenCalled();
    reload();
    expect(update).toHaveBeenCalledWith(true);
  });
});
