import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';

// Roboto / Roboto Mono per ADR-0005, self-hosted through @fontsource rather than a font CDN. This is a
// self-hosted, single-operator system: it must render the same with no route to the public internet,
// and a webfont request is one more third party that sees the operator load the cockpit.
//
// Latin subsets and three weights only. The full package also ships Cyrillic, Greek, Vietnamese and
// math -- roughly forty extra font files for a cockpit whose entire vocabulary is instrument symbols
// and numbers. Add a subset when a surface needs one, not before.
import '@fontsource/roboto/latin-400.css';
import '@fontsource/roboto/latin-500.css';
import '@fontsource/roboto/latin-700.css';
import '@fontsource/roboto-mono/latin-400.css';
import '@fontsource/roboto-mono/latin-500.css';

import { App } from './App';
import { registerAppShellServiceWorker } from './registerSW';
import './index.css';

const rootElement = document.getElementById('root');

// Fail loudly rather than mounting nothing: a missing #root means index.html and this entry point
// have drifted apart, and a silently blank page is the hardest version of that bug to diagnose.
if (!rootElement) {
  throw new Error('Cannot mount the SPA: index.html has no #root element.');
}

createRoot(rootElement).render(
  <StrictMode>
    <BrowserRouter>
      <App />
    </BrowserRouter>
  </StrictMode>,
);

// Register the app-shell service worker (gh#650, R-19). Presentation-only: it caches the shell for an
// offline-capable launch and never caches server state. Registered after mount so a failed registration cannot
// block first paint.
registerAppShellServiceWorker();
