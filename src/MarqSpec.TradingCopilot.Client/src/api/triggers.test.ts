import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./client', () => ({ request: vi.fn(), requestJson: vi.fn() }));

import { request, requestJson } from './client';
import {
  type NewTrigger,
  TriggerConfirmation,
  confirmTrigger,
  createTrigger,
  deleteTrigger,
  listTriggers,
  willFire,
} from './triggers';

const requestJsonMock = vi.mocked(requestJson);
const requestMock = vi.mocked(request);

/** Real GUIDs: `accountId` binds to `Guid?` and the id routes are constrained `{id:guid}`, so anything else is
 * refused before a handler runs. Fixtures that could not survive the real endpoint teach the wrong shape. */
const ACCOUNT = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';
const TRIGGER_ID = '9c5b94b1-35ad-49bb-b118-8e8fc24abf80';

/**
 * The wire contract of the triggers client.
 *
 * This module was the only one in `src/api/` without a test, and the gh#1005 review showed what that cost: a
 * mutation pointing `createTrigger` at `/api/NOT-triggers` **and dropping the body entirely** survived the whole
 * 869-test suite. Every surface test mocks this module, so nothing above it can notice where it actually posts.
 *
 * So these assert the **method, the path, and the payload** — the three things a component test structurally
 * cannot see.
 */
function body(): NewTrigger {
  return {
    symbol: 'ES',
    indicator: 'rsi',
    period: 14,
    resolutionMinutes: 5,
    comparison: 2,
    threshold: 70,
    route: 1,
    hysteresis: null,
    accountId: null,
    size: null,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  requestJsonMock.mockResolvedValue({ ok: true, data: undefined as never });
  requestMock.mockResolvedValue({ ok: true, data: undefined });
});

describe('the triggers wire contract', () => {
  it('lists from the collection route', async () => {
    await listTriggers();

    expect(requestJsonMock).toHaveBeenCalledWith('GET', '/api/triggers');
  });

  it('creates by POSTing the trigger to the collection route, body intact', async () => {
    const sent = body();

    await createTrigger(sent);

    expect(requestJsonMock).toHaveBeenCalledTimes(1);
    const [method, path, payload] = requestJsonMock.mock.calls[0];
    expect(method).toBe('POST');
    expect(path).toBe('/api/triggers');
    // The whole body, not a spot-check: a payload that loses one field is refused by the server, and a component
    // test cannot see which field went missing.
    expect(payload).toEqual(sent);
  });

  it('carries the agent-review fields when they are set', async () => {
    // The route the surface tests never exercise successfully — both of their agent-review cases assert refusals,
    // so the TRUE branch of the form's conditional had nothing pinning it (gh#1005 review).
    const sent: NewTrigger = { ...body(), route: 2, accountId: ACCOUNT, size: 3 };

    await createTrigger(sent);

    const payload = requestJsonMock.mock.calls[0][2] as NewTrigger;
    expect(payload.route).toBe(2);
    expect(payload.accountId).toBe(ACCOUNT);
    expect(payload.size).toBe(3);
  });

  it('confirms via the id-scoped confirm route', async () => {
    await confirmTrigger(TRIGGER_ID);

    expect(requestJsonMock).toHaveBeenCalledWith('POST', `/api/triggers/${TRIGGER_ID}/confirm`);
  });

  it('deletes via the id-scoped route, and does not expect a payload', async () => {
    // Delete is a genuine 204, so it takes `request` rather than `requestJson` — using the payload-requiring
    // wrapper would turn every successful delete into a failed read (gh#963).
    await deleteTrigger(TRIGGER_ID);

    expect(requestMock).toHaveBeenCalledWith('DELETE', `/api/triggers/${TRIGGER_ID}`);
    expect(requestJsonMock).not.toHaveBeenCalled();
  });

  describe('willFire', () => {
    const trigger = (enabled: boolean, confirmation: number) =>
      ({ enabled, confirmation }) as Parameters<typeof willFire>[0];

    it('is true only when the trigger is both confirmed and enabled', () => {
      expect(willFire(trigger(true, TriggerConfirmation.Confirmed))).toBe(true);
    });

    it.each([
      { enabled: true, confirmation: TriggerConfirmation.Unconfirmed, why: 'unconfirmed is inert' },
      { enabled: false, confirmation: TriggerConfirmation.Confirmed, why: 'switched off' },
      { enabled: false, confirmation: TriggerConfirmation.Unconfirmed, why: 'neither' },
    ])('is false when $why', ({ enabled, confirmation }) => {
      // Both directions pinned: dropping either conjunct from `willFire` would let a trigger that cannot fire
      // render as Live, which is the one thing this surface exists to prevent.
      expect(willFire(trigger(enabled, confirmation))).toBe(false);
    });
  });
});
