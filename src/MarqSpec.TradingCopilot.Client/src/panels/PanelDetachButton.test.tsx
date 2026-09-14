import { cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { renderWithProviders } from '../testing/render';
import { PanelDetachButton } from './PanelDetachButton';

afterEach(cleanup);

describe('PanelDetachButton', () => {
  it('pops the panel out when it is docked', () => {
    const onDetach = vi.fn();
    renderWithProviders(
      <PanelDetachButton
        panelId="blotter"
        label="Positions & orders"
        detached={false}
        onDetach={onDetach}
        onReattach={vi.fn()}
      />,
    );

    fireEvent.click(
      screen.getByRole('button', { name: /pop out positions & orders into its own window/i }),
    );

    expect(onDetach).toHaveBeenCalledWith('blotter');
  });

  it('reattaches the panel when it is detached, naming the action in its accessible label', () => {
    const onReattach = vi.fn();
    renderWithProviders(
      <PanelDetachButton
        panelId="blotter"
        label="Positions & orders"
        detached={true}
        onDetach={vi.fn()}
        onReattach={onReattach}
      />,
    );

    // The control has toggled: it now offers reattach, not another pop-out.
    expect(screen.queryByTestId('detach-blotter')).toBeNull();
    fireEvent.click(
      screen.getByRole('button', { name: /reattach positions & orders into this window/i }),
    );

    expect(onReattach).toHaveBeenCalledWith('blotter');
  });
});
