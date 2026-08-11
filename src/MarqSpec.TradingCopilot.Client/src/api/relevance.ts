import { type ApiResult, request } from './client';

/**
 * News relevance configuration (gh#742, of gh#658; R-2/R-6/R-7, gh#359): the ticker↔instrument maps and the topics
 * that decide which news attaches to which instrument, and how loudly. This is **deployment-global** config, not
 * account-scoped — `NewsTopic` and `TickerInstrumentMap` are shared across the deployment — so nothing here is
 * keyed to the active account.
 *
 * Enums serialize as their **integer** value (no `JsonStringEnumConverter` server-side), the same wire convention
 * as the rest of the client.
 */

/** A topic's reach. `Unknown` is a refusable zero — a topic must declare instrument-scoped or global. */
export const TopicScope = { Unknown: 0, Instrument: 1, Global: 2 } as const;
export type TopicScope = (typeof TopicScope)[keyof typeof TopicScope];

/** Star or mute a feed item. `Unknown` is a refusable zero — feedback must declare its intent. */
export const SoftSignalKind = { Unknown: 0, Star: 1, Mute: 2 } as const;
export type SoftSignalKind = (typeof SoftSignalKind)[keyof typeof SoftSignalKind];

/** A ticker↔instrument map: the provider ticker (`SPY`) and the traded instrument it attaches news to (`ES`). */
export interface TickerMap {
  readonly ticker: string;
  readonly instrument: string;
}

/** A relevance topic — a named keyword set, instrument-scoped or global. */
export interface Topic {
  readonly id: string;
  readonly name: string;
  readonly keywords: readonly string[];
  readonly scope: TopicScope;
  /** The instrument for an instrument-scoped topic; `null` for a global one. */
  readonly instrument: string | null;
}

/** A topic create / edit request. PATCH replaces the topic whole (the server never merges). */
export interface TopicInput {
  readonly name: string;
  readonly keywords: readonly string[];
  readonly scope: TopicScope;
  readonly instrument: string | null;
}

/** Every ticker↔instrument map (deployment-global). */
export function listMaps(): Promise<ApiResult<TickerMap[]>> {
  return request<TickerMap[]>('GET', '/api/relevance/maps');
}

/** Creates a ticker↔instrument map. The server normalizes ticker/instrument casing. */
export function createMap(map: TickerMap): Promise<ApiResult<TickerMap>> {
  return request<TickerMap>('POST', '/api/relevance/maps', map);
}

/**
 * Deletes a ticker↔instrument map. The pair is the map's key, carried in the path (upper-cased server-side), so a
 * delete names both halves — there is no id.
 */
export function deleteMap(ticker: string, instrument: string): Promise<ApiResult<void>> {
  return request<void>(
    'DELETE',
    `/api/relevance/maps/${encodeURIComponent(ticker)}/${encodeURIComponent(instrument)}`,
  );
}

/** Every relevance topic (deployment-global). */
export function listTopics(): Promise<ApiResult<Topic[]>> {
  return request<Topic[]>('GET', '/api/relevance/topics');
}

/** Creates a topic. */
export function createTopic(topic: TopicInput): Promise<ApiResult<Topic>> {
  return request<Topic>('POST', '/api/relevance/topics', topic);
}

/** Replaces a topic whole (PATCH — the server never merges a partial). */
export function updateTopic(id: string, topic: TopicInput): Promise<ApiResult<Topic>> {
  return request<Topic>('PATCH', `/api/relevance/topics/${id}`, topic);
}

/** Deletes a topic. */
export function deleteTopic(id: string): Promise<ApiResult<void>> {
  return request<void>('DELETE', `/api/relevance/topics/${id}`);
}
