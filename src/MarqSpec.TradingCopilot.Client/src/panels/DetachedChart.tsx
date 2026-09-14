import { ChartWorkspace, DEFAULT_INSTRUMENT } from '../chart/ChartWorkspace';
import { readLinkedInstrumentSeed, useLinkedInstrument } from './linkedInstrument';

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
  // gh#1017: seed on the opener's last instrument (mirrored to sessionStorage, which a window.open()ed child inherits
  // a copy of at open) so a chart detached while the docked window shows NQ opens on NQ, not the default. Neither
  // window is authoritative — the seed is passive shared storage; ongoing sync rides the linked-instrument channel.
  const [instrument, setInstrument] = useLinkedInstrument(
    readLinkedInstrumentSeed() ?? DEFAULT_INSTRUMENT,
  );
  return <ChartWorkspace instrument={instrument} onInstrument={setInstrument} />;
}
