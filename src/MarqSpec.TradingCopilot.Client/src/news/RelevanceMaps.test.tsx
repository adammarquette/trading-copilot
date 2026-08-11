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
