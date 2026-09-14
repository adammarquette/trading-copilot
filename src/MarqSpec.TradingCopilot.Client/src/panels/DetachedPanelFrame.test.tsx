import { cleanup, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { renderWithProviders } from '../testing/render';
import { DetachedPanelFrame } from './DetachedPanelFrame';

// Globals off ⇒ no RTL auto-cleanup; clear the DOM between renders so a getBy* never matches a prior test's frame.
afterEach(cleanup);

// The safety controls + connection chrome pull realtime / account / API context; stub them so the FRAME is tested
// in isolation. What matters here is that the frame MOUNTS the safety strip, not what each control renders — those
// have their own suites.
vi.mock('../safety/KillSwitchControl', () => ({
  KillSwitchControl: () => <div data-testid="kill-switch-stub" />,
}));
vi.mock('../safety/TimeToFlat', () => ({
  TimeToFlat: () => <div data-testid="time-to-flat-stub" />,
}));
vi.mock('../safety/ProtectionStatus', () => ({ ProtectionStatus: () => <div /> }));
vi.mock('../health/ConnectionStatus', () => ({ ConnectionStatus: () => <div /> }));

describe('DetachedPanelFrame', () => {
  it('titles the window and renders the panel body', () => {
    renderWithProviders(
      <DetachedPanelFrame title="Positions & orders">
        <div data-testid="body" />
      </DetachedPanelFrame>,
    );

    expect(screen.getByRole('heading', { name: 'Positions & orders' })).toBeTruthy();
    expect(screen.getByTestId('body')).toBeTruthy();
  });

  it('carries the safety strip — the kill switch and the countdown are in EVERY window (R-13, ADR-0006)', () => {
    renderWithProviders(
      <DetachedPanelFrame title="x">
        <div />
      </DetachedPanelFrame>,
    );

    const safety = screen.getByRole('region', { name: 'Safety controls' });
    expect(safety.querySelector('[data-safety-slot="kill-switch"]')).toBeTruthy();
    expect(safety.querySelector('[data-safety-slot="time-to-flat"]')).toBeTruthy();
    // ...and the controls themselves are mounted into those slots, not just the empty reservations.
    expect(screen.getByTestId('kill-switch-stub')).toBeTruthy();
    expect(screen.getByTestId('time-to-flat-stub')).toBeTruthy();
  });
});
