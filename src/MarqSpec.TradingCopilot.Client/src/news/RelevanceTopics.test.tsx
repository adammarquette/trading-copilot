import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  createTopic,
  deleteTopic,
  listTopics,
  type Topic,
  TopicScope,
  updateTopic,
} from '../api/relevance';
import { renderWithProviders } from '../testing/render';
import { RelevanceTopics } from './RelevanceTopics';

vi.mock('../api/relevance', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/relevance')>()),
  listTopics: vi.fn(),
  createTopic: vi.fn(),
  updateTopic: vi.fn(),
  deleteTopic: vi.fn(),
}));

const listMock = vi.mocked(listTopics);
const createMock = vi.mocked(createTopic);
const updateMock = vi.mocked(updateTopic);
const deleteMock = vi.mocked(deleteTopic);

const GLOBAL_TOPIC: Topic = {
  id: 't1',
  name: 'FOMC',
  keywords: ['fomc', 'powell'],
  scope: TopicScope.Global,
  instrument: null,
};
const INSTRUMENT_TOPIC: Topic = {
  id: 't2',
  name: 'Fed policy',
  keywords: ['rate'],
  scope: TopicScope.Instrument,
  instrument: 'ES',
};

/** A promise whose resolution we drive by hand, so we can assert the UI mid-request. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}

function fill(label: RegExp, value: string) {
  fireEvent.change(screen.getByLabelText(label, { exact: false }), { target: { value } });
}

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(cleanup);

describe('RelevanceTopics', () => {
  it('lists topics grouped into global and per-instrument', async () => {
    listMock.mockResolvedValue({ ok: true, data: [GLOBAL_TOPIC, INSTRUMENT_TOPIC] });

    renderWithProviders(<RelevanceTopics />);

    expect(await screen.findByText('FOMC')).toBeTruthy();
    expect(screen.getByText('Fed policy')).toBeTruthy();
    // The two group headings — global vs the instrument the topic is scoped to.
    expect(screen.getByText(/global topics/i)).toBeTruthy();
    expect(screen.getByText(/topics for ES/i)).toBeTruthy();
  });

  it('shows an empty state when there are no topics', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });

    renderWithProviders(<RelevanceTopics />);

    expect(await screen.findByText(/no topics yet/i)).toBeTruthy();
  });

  it('surfaces a load error with a retry', async () => {
    listMock.mockResolvedValue({ ok: false, kind: 'failed', status: 500, error: 'boom' });

    renderWithProviders(<RelevanceTopics />);

    expect(await screen.findByText('Could not load the topics')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });

  it('creates a global topic, parsing comma-separated keywords, and refetches', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });
    createMock.mockResolvedValue({ ok: true, data: GLOBAL_TOPIC });

    renderWithProviders(<RelevanceTopics />);
    await screen.findByText(/no topics yet/i);

    fill(/topic name/i, 'FOMC');
    fill(/keywords/i, 'fomc, powell');
    fireEvent.click(screen.getByRole('button', { name: /add topic/i }));

    await waitFor(() =>
      expect(createMock).toHaveBeenCalledWith({
        name: 'FOMC',
        keywords: ['fomc', 'powell'],
        scope: TopicScope.Global,
        instrument: null,
      }),
    );
    expect(listMock).toHaveBeenCalledTimes(2); // initial load + reload after create
  });

  it('creates an instrument-scoped topic, sending the instrument it is scoped to', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });
    createMock.mockResolvedValue({ ok: true, data: INSTRUMENT_TOPIC });

    renderWithProviders(<RelevanceTopics />);
    await screen.findByText(/no topics yet/i);

    // Switch to instrument scope — the instrument field appears and becomes required.
    fireEvent.click(screen.getByRole('button', { name: /instrument/i }));
    fill(/topic name/i, 'Fed policy');
    fill(/keywords/i, 'rate');
    fill(/instrument/i, 'ES');
    fireEvent.click(screen.getByRole('button', { name: /add topic/i }));

    await waitFor(() =>
      expect(createMock).toHaveBeenCalledWith({
        name: 'Fed policy',
        keywords: ['rate'],
        scope: TopicScope.Instrument,
        instrument: 'ES',
      }),
    );
  });

  it('keeps Add disabled until an instrument-scoped topic has both a name and an instrument', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });

    renderWithProviders(<RelevanceTopics />);
    await screen.findByText(/no topics yet/i);

    const add = screen.getByRole('button', { name: /add topic/i }) as HTMLButtonElement;
    expect(add.disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: /instrument/i }));
    fill(/topic name/i, 'Fed policy');
    expect(add.disabled).toBe(true); // instrument still missing
    fill(/instrument/i, 'ES');
    expect(add.disabled).toBe(false);
  });

  it('surfaces a refused create rather than pretending it worked', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });
    createMock.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 409,
      reason: 'A topic named FOMC already exists.',
    });

    renderWithProviders(<RelevanceTopics />);
    await screen.findByText(/no topics yet/i);

    fill(/topic name/i, 'FOMC');
    fireEvent.click(screen.getByRole('button', { name: /add topic/i }));

    expect(await screen.findByText(/already exists/i)).toBeTruthy();
  });

  it('edits a topic in place — populating the form and saving via updateTopic', async () => {
    listMock.mockResolvedValue({ ok: true, data: [INSTRUMENT_TOPIC] });
    updateMock.mockResolvedValue({ ok: true, data: INSTRUMENT_TOPIC });

    renderWithProviders(<RelevanceTopics />);
    await screen.findByText('Fed policy');

    fireEvent.click(screen.getByRole('button', { name: /edit the Fed policy topic/i }));
    // The form is now populated from the topic — its keywords joined back for editing.
    expect((screen.getByLabelText(/keywords/i, { exact: false }) as HTMLInputElement).value).toBe(
      'rate',
    );
    fill(/keywords/i, 'rate, yields');
    fireEvent.click(screen.getByRole('button', { name: /save topic/i }));

    await waitFor(() =>
      expect(updateMock).toHaveBeenCalledWith('t2', {
        name: 'Fed policy',
        keywords: ['rate', 'yields'],
        scope: TopicScope.Instrument,
        instrument: 'ES',
      }),
    );
    expect(listMock).toHaveBeenCalledTimes(2);
  });

  it('deletes a topic by id and refetches', async () => {
    listMock.mockResolvedValue({ ok: true, data: [GLOBAL_TOPIC] });
    deleteMock.mockResolvedValue({ ok: true, data: undefined });

    renderWithProviders(<RelevanceTopics />);
    await screen.findByText('FOMC');

    fireEvent.click(screen.getByRole('button', { name: /delete the FOMC topic/i }));

    await waitFor(() => expect(deleteMock).toHaveBeenCalledWith('t1'));
    expect(listMock).toHaveBeenCalledTimes(2);
  });

  it('guards Add while a create is in flight, so a double-click cannot fire two POSTs', async () => {
    listMock.mockResolvedValue({ ok: true, data: [] });
    const pending = deferred<Awaited<ReturnType<typeof createTopic>>>();
    createMock.mockReturnValue(pending.promise);

    renderWithProviders(<RelevanceTopics />);
    await screen.findByText(/no topics yet/i);

    fill(/topic name/i, 'FOMC');
    const add = screen.getByRole('button', { name: /add topic/i }) as HTMLButtonElement;
    fireEvent.click(add);

    expect(add.disabled).toBe(true);
    fireEvent.click(add);
    expect(createMock).toHaveBeenCalledTimes(1);

    pending.resolve({ ok: true, data: GLOBAL_TOPIC });
    await waitFor(() => expect(listMock).toHaveBeenCalledTimes(2));
  });
});
