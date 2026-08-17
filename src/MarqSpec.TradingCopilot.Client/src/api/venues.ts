import { type ApiResult, requestJson } from './client';

/**
 * A venue's onboarding setup contract as served by `GET /venues/setup` (gh#64, ADR-0023): its identity and the
 * labelled credential schema. It carries **no credential value** — only the shape of what a login needs and how to
 * label it, so ProjectX's "`ApiKey` is really the username" trap is a label the operator reads rather than folklore
 * in an env comment (ADR-0015). The response is served as an anonymous object, so its shape is named here.
 */
export interface VenueSetup {
  readonly venueId: string;
  readonly displayName: string;
  readonly credentials: readonly VenueCredentialField[];
}

/** One credential field's schema: its config key, the human label, whether it is a secret / required, and help. */
export interface VenueCredentialField {
  readonly key: string;
  readonly label: string;
  readonly secret: boolean;
  readonly required: boolean;
  readonly helpText: string | null;
}

/**
 * The registered venues' setup contracts — identity + labelled credential schema, for onboarding to render from
 * (never a secret). A venue appears the moment its adapter is compiled in and its contract registered (ADR-0023).
 */
export function getVenueSetup(): Promise<ApiResult<VenueSetup[]>> {
  return requestJson<VenueSetup[]>('GET', '/venues/setup');
}
