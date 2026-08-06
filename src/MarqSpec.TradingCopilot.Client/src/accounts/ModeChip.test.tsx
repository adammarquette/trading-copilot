import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { TradingMode } from '../api/accounts';
import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import { ModeChip } from './ModeChip';

function renderChip(mode: TradingMode) {
  return render(
    <ThemeModeProvider initialMode="dark">
      <ModeChip mode={mode} />
    </ThemeModeProvider>,
  );
}

afterEach(() => {
  cleanup();
});

describe('ModeChip', () => {
  it('labels a practice account', () => {
    renderChip(TradingMode.Practice);

    expect(screen.getByTestId('mode-chip').dataset.mode).toBe('Practice');
  });

  it('labels a live account', () => {
    renderChip(TradingMode.Live);

    expect(screen.getByTestId('mode-chip').dataset.mode).toBe('Live');
    expect(screen.getByTestId('mode-chip').textContent).toContain('Live');
  });

  it('labels an undeclared account', () => {
    renderChip(TradingMode.Undeclared);

    expect(screen.getByTestId('mode-chip').dataset.mode).toBe('Undeclared');
  });
});
