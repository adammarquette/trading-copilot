import { describe, expect, it } from 'vitest';

import { ConditionalCrossDirection } from '../api/orders';
import {
  SELECTABLE_DIRECTIONS,
  describeTrigger,
  directionLabel,
  isTriggerFireable,
} from './conditional';

describe('SELECTABLE_DIRECTIONS', () => {
  it('offers only the two firing directions — never Unknown, the refusable zero that rests forever', () => {
    expect(SELECTABLE_DIRECTIONS).toEqual([
      ConditionalCrossDirection.RisesTo,
      ConditionalCrossDirection.FallsTo,
    ]);
    expect(SELECTABLE_DIRECTIONS).not.toContain(ConditionalCrossDirection.Unknown);
  });
});

describe('directionLabel', () => {
  it('labels the two firing directions', () => {
    expect(directionLabel(ConditionalCrossDirection.RisesTo)).toBe('Rises to');
    expect(directionLabel(ConditionalCrossDirection.FallsTo)).toBe('Falls to');
  });
});

describe('describeTrigger', () => {
  it('phrases the trigger from the server direction NAME — a breakout rises to, a pullback falls to', () => {
    expect(describeTrigger('RisesTo', 5010)).toBe('rises to 5010');
    expect(describeTrigger('FallsTo', 4990)).toBe('falls to 4990');
  });

  it('still reads sensibly for an unrecognised direction rather than rendering blank', () => {
    // If the server grows a third direction before this client does, the operator gets something specific enough to
    // act on ("crosses 5010") instead of an empty phrase.
    expect(describeTrigger('Sideways', 5010)).toBe('crosses 5010');
  });
});

describe('isTriggerFireable', () => {
  it('accepts a positive price with a real direction', () => {
    expect(isTriggerFireable(5010, ConditionalCrossDirection.RisesTo)).toBe(true);
    expect(isTriggerFireable(4990, ConditionalCrossDirection.FallsTo)).toBe(true);
  });

  it('refuses Unknown — a direction that never fires would rest forever', () => {
    expect(isTriggerFireable(5010, ConditionalCrossDirection.Unknown)).toBe(false);
  });

  it('refuses a non-positive or non-finite price', () => {
    expect(isTriggerFireable(0, ConditionalCrossDirection.RisesTo)).toBe(false);
    expect(isTriggerFireable(-5, ConditionalCrossDirection.RisesTo)).toBe(false);
    expect(isTriggerFireable(Number.NaN, ConditionalCrossDirection.RisesTo)).toBe(false);
  });
});
