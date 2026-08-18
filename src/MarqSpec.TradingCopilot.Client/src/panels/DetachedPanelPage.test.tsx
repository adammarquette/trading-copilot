import { cleanup, screen } from '@testing-library/react';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { renderWithProviders } from '../testing/render';
import { DetachedPanelPage } from './DetachedPanelPage';

// Vitest runs with globals off in this project, so RTL does not auto-clean between tests — do it explicitly, or a
// prior render's frame (its safety strip included) lingers and turns a getBy* into a multiple-match.
afterEach(cleanup);

// Resolve against a stub registry, so the page's routing logic is tested rather than the real panels' data
// dependencies (each real panel has its own suite).
vi.mock('./panelRegistry', () => ({
  panelById: (id: string | undefined) =>
    id === 'known'
      ? { id: 'known', title: 'Known Panel', Panel: () => <div data-testid="panel-body" /> }
      : undefined,
}));
// The frame's safety controls need realtime / account context; stub them (the frame itself is tested separately).
vi.mock('../safety/KillSwitchControl', () => ({
  KillSwitchControl: () => <div data-testid="kill-switch-stub" />,
}));
vi.mock('../safety/TimeToFlat', () => ({ TimeToFlat: () => <div /> }));
vi.mock('../safety/ProtectionStatus', () => ({ ProtectionStatus: () => <div /> }));
vi.mock('../health/ConnectionStatus', () => ({ ConnectionStatus: () => <div /> }));

function renderAt(route: string) {
  return renderWithProviders(
    <Routes>
      <Route path="/panel/:panelId" element={<DetachedPanelPage />} />
    </Routes>,
    { route },
  );
}

describe('DetachedPanelPage', () => {
  it('frames the resolved panel under its title', () => {
    renderAt('/panel/known');

    expect(screen.getByRole('heading', { name: 'Known Panel' })).toBeTruthy();
    expect(screen.getByTestId('panel-body')).toBeTruthy();
  });

  it('shows a not-found for an unknown panel id — but STILL inside the safety-strip frame', () => {
    renderAt('/panel/mystery');

    expect(screen.getByText('No such panel')).toBeTruthy();
    // The mistyped pop-out keeps its kill switch: a wrong address is not a reason to drop the safety controls.
    expect(screen.getByTestId('kill-switch-stub')).toBeTruthy();
  });
});
