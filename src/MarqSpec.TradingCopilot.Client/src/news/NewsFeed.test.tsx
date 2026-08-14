import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { clearNewsFeedback, getNewsFeed, setNewsFeedback, SoftSignalKind } from '../api/news';
import { NewsFeed } from './NewsFeed';

vi.mock('../api/news', async (importOriginal) => ({
  // SoftSignalKind stays REAL -- the star/mute integer contract is a tested property, not something to stub.
  ...(await importOriginal<typeof import('../api/news')>()),
  getNewsFeed: vi.fn(),
  setNewsFeedback: vi.fn(),
  clearNewsFeedback: vi.fn(),
}));

const feed = vi.mocked(getNewsFeed);
const setFeedback = vi.mocked(setNewsFeedback);
const clearFeedback = vi.mocked(clearNewsFeedback);

function item(overrides: Record<string, unknown> = {}) {
  return {
    dedupKey: 'k1',
    title: 'ES breaks out',
    url: 'https://example.test/es',
    publishedAt: '2026-08-14T00:00:00Z',
    instruments: ['ES'],
    topics: ['breakout'],
    sources: ['finnhub'],
    baseRelevance: 1,
    multiplier: 1,
    salience: 1,
    whyWeighted: 'Base relevance — no matching feedback yet.',
    reasons: [],
    feedback: null as SoftSignalKind | null,
    ...overrides,
  };
}

async function renderFeed() {
  const view = render(<NewsFeed />);
  await act(async () => {});
  return view;
}

beforeEach(() => {
  feed.mockResolvedValue({ ok: true, data: [item()] });
  setFeedback.mockResolvedValue({ ok: true, data: undefined });
  clearFeedback.mockResolvedValue({ ok: true, data: undefined });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('NewsFeed', () => {
  it('renders each ranked item with its title, source and the explained weighting', async () => {
    await renderFeed();

    const row = screen.getByTestId('news-item');
    expect(row.textContent).toContain('ES breaks out');
    expect(row.textContent).toContain('finnhub');
    // The weighting is explained, never a hidden score (ADR-0014).
    expect(row.textContent).toContain('Base relevance');
    expect(row.getAttribute('data-feedback')).toBe('none');
  });

  it('stars an unrated item and reflects it, without re-fetching the feed', async () => {
    await renderFeed();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^star this item$/i }));
    });

    expect(setFeedback).toHaveBeenCalledWith('k1', SoftSignalKind.Star);
    expect(screen.getByTestId('news-item').getAttribute('data-feedback')).toBe('star');
    expect(feed).toHaveBeenCalledTimes(1); // the rating updates in place; the list is not reshuffled mid-glance
  });

  it('mutes an item, sending the mute kind', async () => {
    await renderFeed();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^mute similar items$/i }));
    });

    expect(setFeedback).toHaveBeenCalledWith('k1', SoftSignalKind.Mute);
    expect(screen.getByTestId('news-item').getAttribute('data-feedback')).toBe('mute');
  });

  it('clears the rating when the active one is clicked again — the feedback round-trips', async () => {
    feed.mockResolvedValue({ ok: true, data: [item({ feedback: SoftSignalKind.Star })] });
    await renderFeed();
    expect(screen.getByTestId('news-item').getAttribute('data-feedback')).toBe('star');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^un-star this item$/i }));
    });

    expect(clearFeedback).toHaveBeenCalledWith('k1');
    expect(setFeedback).not.toHaveBeenCalled();
    expect(screen.getByTestId('news-item').getAttribute('data-feedback')).toBe('none');
  });

  it('replaces a mute with a star via the upsert, not a clear', async () => {
    feed.mockResolvedValue({ ok: true, data: [item({ feedback: SoftSignalKind.Mute })] });
    await renderFeed();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^star this item$/i }));
    });

    expect(setFeedback).toHaveBeenCalledWith('k1', SoftSignalKind.Star);
    expect(clearFeedback).not.toHaveBeenCalled();
    expect(screen.getByTestId('news-item').getAttribute('data-feedback')).toBe('star');
  });

  it('surfaces a refused rating and leaves the item unrated', async () => {
    setFeedback.mockResolvedValue({
      ok: false,
      kind: 'refused',
      status: 404,
      reason: 'No news item with that key.',
    });
    await renderFeed();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^star this item$/i }));
    });

    expect(screen.getByTestId('news-feed').textContent).toContain('No news item with that key.');
    expect(screen.getByTestId('news-item').getAttribute('data-feedback')).toBe('none');
  });

  it('does not rate twice when clicked again before the first resolves', async () => {
    let release: (value: { ok: true; data: undefined }) => void = () => {};
    setFeedback.mockReturnValue(
      new Promise((resolve) => {
        release = resolve;
      }),
    );

    await renderFeed();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /^star this item$/i }));
      fireEvent.click(screen.getByRole('button', { name: /^star this item$/i }));
    });

    expect(setFeedback).toHaveBeenCalledTimes(1);
    await act(async () => {
      release({ ok: true, data: undefined });
    });
  });

  it('shows an empty state when the feed has no items', async () => {
    feed.mockResolvedValue({ ok: true, data: [] });
    await renderFeed();

    expect(screen.getByTestId('news-feed').textContent?.toLowerCase()).toContain('no news yet');
  });
});
