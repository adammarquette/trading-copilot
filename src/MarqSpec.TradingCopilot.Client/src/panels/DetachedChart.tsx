import { ChartWorkspace, DEFAULT_INSTRUMENT } from '../chart/ChartWorkspace';
import { useLinkedInstrument } from './linkedInstrument';

/**
 * The central chart as a standalone pop-out panel (gh#1015, ADR-0006). It renders the **same** chart column the
 * docked workspace does ({@link ChartWorkspace}) and drives its instrument through the cross-window linked-instrument
 * channel ({@link useLinkedInstrument}): selecting an instrument in the docked window moves this chart, and vice
 * versa, with **neither window authoritative** — each keeps its own mirror and the last write wins.
 *
 * A no-props panel like the others in the registry ({@link DETACHABLE_PANELS}): it sources everything it needs — the
 * account and realtime connection the overlay hooks read — from the providers the detached `/panel/:panelId` route
 * carries, and it inherits the safety strip from {@link DetachedPanelFrame} for free.
 */
export function DetachedChart(): React.JSX.Element {
  const [instrument, setInstrument] = useLinkedInstrument(DEFAULT_INSTRUMENT);
  return <ChartWorkspace instrument={instrument} onInstrument={setInstrument} />;
}
