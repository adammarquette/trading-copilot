import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import type { VenueSetup } from '../api/venues';
import { VenueCredentialGuidance } from './VenueCredentialGuidance';

const PROJECTX: VenueSetup = {
  venueId: 'projectx',
  displayName: 'ProjectX / TopstepX',
  credentials: [
    {
      key: 'ApiKey',
      label: 'Username',
      secret: false,
      required: true,
      helpText: 'Your TopstepX username. Sent to the gateway as "username".',
    },
    {
      key: 'ApiSecret',
      label: 'API key',
      secret: true,
      required: true,
      helpText: 'Your TopstepX API key.',
    },
  ],
};

const WITH_OPTIONAL: VenueSetup = {
  venueId: 'demo',
  displayName: 'Demo venue',
  credentials: [
    { key: 'Required', label: 'A required field', secret: false, required: true, helpText: null },
    { key: 'Optional', label: 'An optional field', secret: false, required: false, helpText: null },
  ],
};

afterEach(cleanup);

describe('VenueCredentialGuidance', () => {
  it('maps each config key to its true label, so the naming trap is legible', () => {
    render(<VenueCredentialGuidance venue={PROJECTX} />);

    // The trap: the field named ApiKey actually holds the username, and ApiSecret the API key.
    const apiKeyRow = screen.getByTestId('venue-credential-ApiKey');
    expect(within(apiKeyRow).getByText('ApiKey')).toBeTruthy();
    expect(within(apiKeyRow).getByText('Username')).toBeTruthy();

    const apiSecretRow = screen.getByTestId('venue-credential-ApiSecret');
    expect(within(apiSecretRow).getByText('ApiSecret')).toBeTruthy();
    expect(within(apiSecretRow).getByText('API key')).toBeTruthy();
  });

  it('marks the secret field as secret, and does not mark the non-secret one', () => {
    render(<VenueCredentialGuidance venue={PROJECTX} />);

    // Exact 'secret' — a regex would also match "ApiSecret", the config key.
    expect(
      within(screen.getByTestId('venue-credential-ApiSecret')).getByText('secret'),
    ).toBeTruthy();
    expect(within(screen.getByTestId('venue-credential-ApiKey')).queryByText('secret')).toBeNull();
  });

  it('shows each field help text', () => {
    render(<VenueCredentialGuidance venue={PROJECTX} />);

    expect(screen.getByText(/sent to the gateway as "username"/i)).toBeTruthy();
    expect(screen.getByText(/your topstepx api key/i)).toBeTruthy();
  });

  it('names the venue so the operator knows which login these fields are for', () => {
    render(<VenueCredentialGuidance venue={PROJECTX} />);

    expect(screen.getByText(/ProjectX \/ TopstepX/i)).toBeTruthy();
  });

  it('marks an optional field optional, and leaves required fields unmarked', () => {
    // ProjectX's fields are all required, so this is invisible today — but the schema carries `required`, and a
    // venue with a genuinely optional field (Tradovate's seven) must not render it identically to a required one.
    render(<VenueCredentialGuidance venue={WITH_OPTIONAL} />);

    expect(
      within(screen.getByTestId('venue-credential-Optional')).getByText('optional'),
    ).toBeTruthy();
    expect(
      within(screen.getByTestId('venue-credential-Required')).queryByText('optional'),
    ).toBeNull();
  });
});
