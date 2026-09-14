import { cleanup, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { Destination } from '../navigation/destinations';
import { renderWithProviders } from '../testing/render';
import { NewsSurface } from './NewsSurface';

// The maps and topics panels are data-fetching components covered in their own tests; this suite is about the
// surface shell and its shell contract, so both are stubbed.
vi.mock('./RelevanceMaps', () => ({
  RelevanceMaps: () => <div data-testid="relevance-maps-stub" />,
}));
vi.mock('./RelevanceTopics', () => ({
  RelevanceTopics: () => <div data-testid="relevance-topics-stub" />,
}));

const destination = { id: 'news', label: 'News', path: '/news' } as unknown as Destination;

afterEach(cleanup);

describe('NewsSurface', () => {
  it('tags the surface for the shell and mounts the relevance config', () => {
    renderWithProviders(<NewsSurface destination={destination} />);

    expect(screen.getByTestId('surface').dataset.surface).toBe('news');
    expect(screen.getByTestId('relevance-maps-stub')).toBeTruthy();
    expect(screen.getByTestId('relevance-topics-stub')).toBeTruthy();
  });

  it('says the configuration is deployment-wide, not account-scoped', () => {
    renderWithProviders(<NewsSurface destination={destination} />);

    expect(screen.getByText(/shared across the deployment/i)).toBeTruthy();
  });
});
