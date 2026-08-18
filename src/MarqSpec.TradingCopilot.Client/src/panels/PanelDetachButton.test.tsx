import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { renderWithProviders } from '../testing/render';
import { detachPanel } from './detachPanel';
import { PanelDetachButton } from './PanelDetachButton';

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
