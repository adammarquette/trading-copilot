import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ApiResult } from '../api/client';
import { type Trigger, listTriggers } from '../api/triggers';
import { destinations } from '../navigation/destinations';
import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import { RulebookSurface } from './RulebookSurface';

vi.mock('../api/triggers', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/triggers')>();
  return { ...actual, listTriggers: vi.fn(), confirmTrigger: vi.fn(), deleteTrigger: vi.fn() };
});

const listMock = vi.mocked(listTriggers);

const rulebook = destinations.find((d) => d.id === 'rulebook')!;

function renderSurface() {
  return render(
    <ThemeModeProvider initialMode="dark">
      <RulebookSurface destination={rulebook} />
    </ThemeModeProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  listMock.mockResolvedValue({ ok: true, data: [] } satisfies ApiResult<Trigger[]>);
});

afterEach(cleanup);

describe('RulebookSurface', () => {
  it('carries the shell data-surface contract for its destination', async () => {
    renderSurface();

    const surface = await screen.findByTestId('surface');
    expect(surface.getAttribute('data-surface')).toBe('rulebook');
  });

  it('is reachable from the one navigation table, not a hand-written route', () => {
    // The table is what generates both the route and the rail entry, so a destination without a Surface renders
    // the placeholder. Asserting the wiring here means "built" cannot silently regress to a placeholder.
    expect(rulebook.Surface).toBe(RulebookSurface);
    expect(rulebook.path).toBe('/rulebook');
  });

  it('says the plain-language rules half is not built', async () => {
    // The destination's own summary promises rules AND triggers; only triggers exist (gh#660 is blocked on
    // gh#19). An operator hunting the rules half should learn it is unbuilt rather than assume they mis-navigated,
    // so the disclosure is asserted rather than left to whoever edits the copy next.
    renderSurface();

    expect(await screen.findByText(/Plain-language rules are not built yet/i)).toBeTruthy();
  });

  it('states the confirm-before-live rule above the list', async () => {
    renderSurface();

    expect(await screen.findByText(/does nothing until you confirm it/i)).toBeTruthy();
  });

  it('renders the triggers list inside itself', async () => {
    renderSurface();

    // The empty state belongs to TriggersSurface; seeing it here proves the composition, not just the header.
    expect(await screen.findByTestId('empty-state')).toBeTruthy();
  });
});
