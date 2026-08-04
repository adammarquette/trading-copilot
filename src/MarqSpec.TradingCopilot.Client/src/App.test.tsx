import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { App } from './App';

/** A stand-in for the parts of Response the probe actually reads. */
function healthResponse(status: number): Response {
  return { ok: status >= 200 && status < 300, status } as unknown as Response;
}

function stubFetch(impl: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>) {
  const fetchMock = vi.fn(impl);
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function status() {
  return screen.getByRole('status');
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('App', () => {
  it('renders the application name', () => {
    stubFetch(() => new Promise<Response>(() => {}));

    render(<App />);

    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Trading Co-Pilot');
  });

  it('probes a same-origin /health so the BFF that served the bundle answers it', () => {
    const fetchMock = stubFetch(() => new Promise<Response>(() => {}));

    render(<App />);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    // The value matters more than the call: an absolute URL here would pin the SPA to one host.
    expect(url).toBe('/health');
  });

  it('reports checking until the probe settles', () => {
    stubFetch(() => new Promise<Response>(() => {}));

    render(<App />);

    expect(status().dataset.status).toBe('checking');
  });

  it('reports the BFF reachable when /health answers 200', async () => {
    stubFetch(() => Promise.resolve(healthResponse(200)));

    render(<App />);

    await waitFor(() => {
      expect(status().dataset.status).toBe('reachable');
    });
    expect(status().textContent).toContain('reachable');
  });

  it('reports the BFF unreachable when /health answers an error status', async () => {
    // Guards the difference between "the request completed" and "the BFF is healthy" -- a 503 still
    // resolves the fetch promise, so a naive .then would call a down BFF reachable.
    stubFetch(() => Promise.resolve(healthResponse(503)));

    render(<App />);

    await waitFor(() => {
      expect(status().dataset.status).toBe('unreachable');
    });
  });

  it('reports the BFF unreachable when the probe fails outright', async () => {
    stubFetch(() => Promise.reject(new TypeError('Failed to fetch')));

    render(<App />);

    await waitFor(() => {
      expect(status().dataset.status).toBe('unreachable');
    });
  });

  it('aborts an in-flight probe on unmount', () => {
    let captured: AbortSignal | undefined;
    stubFetch((_input, init) => {
      captured = init?.signal ?? undefined;
      return new Promise<Response>(() => {});
    });

    const { unmount } = render(<App />);
    expect(captured?.aborted).toBe(false);

    unmount();

    expect(captured?.aborted).toBe(true);
  });
});
