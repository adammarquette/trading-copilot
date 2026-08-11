import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { createMap, deleteMap, listMaps, type TickerMap } from '../api/relevance';
import { renderWithProviders } from '../testing/render';
import { RelevanceMaps } from './RelevanceMaps';

vi.mock('../api/relevance', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/relevance')>()),
  listMaps: vi.fn(),
  createMap: vi.fn(),
  deleteMap: vi.fn(),
}));

const listMock = vi.mocked(listMaps);
const createMock = vi.mocked(createMap);
const deleteMock = vi.mocked(deleteMap);

const MAP: TickerMap = { ticker: 'SPY', instrument: 'ES' };

/** A promise whose resolution we drive by hand, so we can assert the UI mid-request. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}

function fillPair(ticker: string, instrument: string) {
  fireEvent.change(screen.getByLabelText(/ticker/i, { exact: false }), {
    target: { value: ticker },
  });
  fireEvent.change(screen.getByLabelText(/instrument/i, { exact: false }), {
    target: { value: instrument },
  });
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(cleanup);

describe('RelevanceMaps', () => {
  it('loads and lists the maps', async () => {
    listMock.mockResolvedValue({ ok: true, data: [MAP] });

    renderWithProviders(<RelevanceMaps />);

    expect(await screen.findByText('SPY → ES')).toBeTruthy();
  });

  it('shows an empty state when there are no maps', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });

    renderWithProviders(<RelevanceMaps />);

    expect(await screen.findByText(/no maps yet/i)).toBeTruthy();
  });

  it('surfaces a load error with a retry', async () => {
    listMock.mockResolvedValue({ ok: false, kind: 'failed', status: 500, error: 'boom' });

    renderWithProviders(<RelevanceMaps />);

    expect(await screen.findByText('Could not load the maps')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });

  it('creates a map and refetches the list', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });
    createMock.mockResolvedValue({ ok: true, data: MAP });

    renderWithProviders(<RelevanceMaps />);
    await screen.findByText(/no maps yet/i);

    fillPair('SPY', 'ES');
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));

    await waitFor(() =>
      expect(createMock).toHaveBeenCalledWith({ ticker: 'SPY', instrument: 'ES' }),
    );
    expect(listMock).toHaveBeenCalledTimes(2); // initial load + reload after create
  });

  it('surfaces a refused create (a conflicting pair) rather than pretending it worked', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });
    createMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'A map for SPY→ES already exists.',
    });

    renderWithProviders(<RelevanceMaps />);
    await screen.findByText(/no maps yet/i);

    fillPair('SPY', 'ES');
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));

    expect(await screen.findByText(/already exists/i)).toBeTruthy();
  });

  it('deletes a map by its pair and refetches', async () => {
    listMock.mockResolvedValue({ ok: true, data: [MAP] });
    deleteMock.mockResolvedValue({ ok: true, data: undefined });

    renderWithProviders(<RelevanceMaps />);
    await screen.findByText('SPY → ES');

    fireEvent.click(screen.getByRole('button', { name: /delete the SPY to ES map/i }));

    await waitFor(() => expect(deleteMock).toHaveBeenCalledWith('SPY', 'ES'));
    expect(listMock).toHaveBeenCalledTimes(2);
  });

  it('guards Add while a create is in flight, so a double-click cannot fire two POSTs', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });
    const pending = deferred<Awaited<ReturnType<typeof createMap>>>();
    createMock.mockReturnValue(pending.promise);

    renderWithProviders(<RelevanceMaps />);
    await screen.findByText(/no maps yet/i);

    fillPair('SPY', 'ES');
    const add = screen.getByRole('button', { name: 'Add' }) as HTMLButtonElement;
    fireEvent.click(add);

    // While the POST is outstanding the button is disabled and re-labelled, so a second click is
    // swallowed rather than firing a duplicate create that comes back 409 on its own success.
    expect(add.disabled).toBe(true);
    expect(add.textContent).toMatch(/adding/i);
    fireEvent.click(add);
    expect(createMock).toHaveBeenCalledTimes(1);

    pending.resolve({ ok: true, data: MAP });
    await waitFor(() => expect(listMock).toHaveBeenCalledTimes(2)); // reload after the create settles
  });

  it('guards a row delete while its DELETE is in flight, so a double-click cannot fire twice', async () => {
    listMock.mockResolvedValue({ ok: true, data: [MAP] });
    const pending = deferred<Awaited<ReturnType<typeof deleteMap>>>();
    deleteMock.mockReturnValue(pending.promise);

    renderWithProviders(<RelevanceMaps />);
    await screen.findByText('SPY → ES');

    const del = screen.getByRole('button', {
      name: /delete the SPY to ES map/i,
    }) as HTMLButtonElement;
    fireEvent.click(del);

    // The row's own button disables while its DELETE is outstanding, so the second click can't fire a
    // duplicate that 404s server-side (the row is already gone) and surfaces a spurious failure.
    expect(del.disabled).toBe(true);
    fireEvent.click(del);
    expect(deleteMock).toHaveBeenCalledTimes(1);

    pending.resolve({ ok: true, data: undefined });
    await waitFor(() => expect(listMock).toHaveBeenCalledTimes(2));
  });

  it('keeps the Add action disabled until both halves are filled', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });

    renderWithProviders(<RelevanceMaps />);
    await screen.findByText(/no maps yet/i);

    const add = screen.getByRole('button', { name: 'Add' }) as HTMLButtonElement;
    expect(add.disabled).toBe(true);
    fillPair('SPY', 'ES');
    expect(add.disabled).toBe(false);
  });
});
