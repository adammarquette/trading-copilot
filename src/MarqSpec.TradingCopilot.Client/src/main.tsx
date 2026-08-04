import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';

import { App } from './App';
import './index.css';

const rootElement = document.getElementById('root');

// Fail loudly rather than mounting nothing: a missing #root means index.html and this entry point
// have drifted apart, and a silently blank page is the hardest version of that bug to diagnose.
if (!rootElement) {
  throw new Error('Cannot mount the SPA: index.html has no #root element.');
}

createRoot(rootElement).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
