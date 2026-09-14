import { cleanup, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { renderWithProviders } from '../testing/render';
import { BehindMarker } from './BehindMarker';

afterEach(cleanup);

describe('BehindMarker', () => {
  it('renders a subtle, non-destructive status announcing the read may be behind', () => {
    renderWithProviders(<BehindMarker />);

    const marker = screen.getByTestId('behind-marker');
    expect(marker.textContent).toContain('may be out of date');
    // A live region, not an alert -- this is deliberately NOT an error screen (R-19 / ADR-0013).
    expect(marker.getAttribute('role')).toBe('status');
  });

  it('takes a caller-supplied test id, so a surface can keep its own established selector', () => {
    renderWithProviders(<BehindMarker testId="suggestions-stale" />);

    expect(screen.getByTestId('suggestions-stale')).toBeTruthy();
    expect(screen.queryByTestId('behind-marker')).toBeNull();
  });
});
