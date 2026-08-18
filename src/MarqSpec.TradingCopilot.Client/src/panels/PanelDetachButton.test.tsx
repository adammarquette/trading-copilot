import { cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { renderWithProviders } from '../testing/render';
import { detachPanel } from './detachPanel';
import { PanelDetachButton } from './PanelDetachButton';

// Globals off ⇒ no RTL auto-cleanup; clear between tests for consistency with the sibling suites.
afterEach(cleanup);

vi.mock('./detachPanel', () => ({ detachPanel: vi.fn() }));
const detach = vi.mocked(detachPanel);

describe('PanelDetachButton', () => {
  it('detaches its panel when clicked, and names the panel in its accessible label', () => {
    renderWithProviders(<PanelDetachButton panelId="blotter" label="Positions & orders" />);

    const button = screen.getByRole('button', {
      name: /pop out positions & orders into its own window/i,
    });
    fireEvent.click(button);

    expect(detach).toHaveBeenCalledWith('blotter');
  });
});
