import { ConditionalCrossDirection } from '../api/orders';

/**
 * Presenting the "send when conditions met" trigger on the order ticket (gh#655, R-11 / R-12, ADR-0007).
 *
 * A conditional entry is **held local, off the book, and re-gated at fire time** — nothing about it is committed
 * when it is created. This module owns the small, pure pieces of that surface: which directions may be offered,
 * how a direction reads, and whether a trigger is even fireable. The honesty of the pending state lives in the
 * component; the wording lives here so it is tested once.
 */

/**
 * The directions a conditional may declare — {@link ConditionalCrossDirection.Unknown} is excluded **by
 * construction**, the same posture as the ticket's `SELECTABLE_ORDER_TYPES`: Unknown never fires, so offering it
 * would let the operator create an order that rests forever.
 */
export const SELECTABLE_DIRECTIONS: readonly ConditionalCrossDirection[] = [
  ConditionalCrossDirection.RisesTo,
  ConditionalCrossDirection.FallsTo,
];

const DIRECTION_LABELS: Readonly<Record<number, string>> = {
  [ConditionalCrossDirection.RisesTo]: 'Rises to',
  [ConditionalCrossDirection.FallsTo]: 'Falls to',
};

/** The label for a direction in the trigger selector. */
export function directionLabel(direction: ConditionalCrossDirection): string {
  return DIRECTION_LABELS[direction] ?? 'Crosses';
}

/**
 * The verb for a direction **name** as the server echoes it (`RisesTo` / `FallsTo`) — the pending panel reads back
 * a name, the same asymmetry as `outcome`. An unrecognised name falls back to a neutral "crosses" so a future
 * server direction still renders something the operator can act on rather than a blank.
 */
const DIRECTION_VERBS: Readonly<Record<string, string>> = {
  RisesTo: 'rises to',
  FallsTo: 'falls to',
};

/** Phrases the trigger for the pending panel — e.g. `rises to 5010`. */
export function describeTrigger(directionName: string, triggerPrice: number): string {
  return `${DIRECTION_VERBS[directionName] ?? 'crosses'} ${triggerPrice}`;
}

/**
 * Whether the operator has declared a **fireable** trigger: a positive, finite price and a real (non-Unknown)
 * direction. The create control is gated on this, so a conditional that could never fire is never sent — the
 * client mirror of the server's construction-time refusal, caught before the round-trip.
 */
export function isTriggerFireable(
  triggerPrice: number,
  direction: ConditionalCrossDirection,
): boolean {
  return (
    Number.isFinite(triggerPrice) &&
    triggerPrice > 0 &&
    direction !== ConditionalCrossDirection.Unknown
  );
}
