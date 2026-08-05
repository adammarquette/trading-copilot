import react from '@vitejs/plugin-react';
import { loadEnv } from 'vite';
import { VitePWA } from 'vite-plugin-pwa';
import { defineConfig } from 'vitest/config';

import { NETWORK_ONLY_PATH } from './src/sw-cache-policy';

export default defineConfig(({ mode }) => {
  // loadEnv folds matching process.env entries in with any .env file, so `VITE_BFF_ORIGIN=... npm run
  // dev` works without one. The '.' env dir keeps this off Node's `process` global, which would drag
  // @types/node into a browser-only project.
  const bffOrigin = loadEnv(mode, '.', 'VITE_').VITE_BFF_ORIGIN;

  return {
    plugins: [
      react(),
      // The installable PWA (gh#650, R-19 / ADR-0010). Workbox generates the worker; registration is explicit
      // (registerSW.ts, from main.tsx), not injected. The cache boundary is NETWORK_ONLY_PATH — a single source of
      // truth shared with the test that asserts "no state read is cache-served".
      VitePWA({
        registerType: 'prompt',
        injectRegister: null,
        workbox: {
          // Precache the app SHELL only — the static bundle assets. No API response is a build artifact, so no
          // server state is ever precached.
          globPatterns: ['**/*.{js,css,html,svg,png,ico,webmanifest,woff,woff2}'],
          navigateFallback: '/index.html',
          // A client-side route is answered with the cached shell offline, but a state path (/api, /health,
          // /ready) is denied it and must reach the network — a stale shell must never stand in for live state.
          navigateFallbackDenylist: [NETWORK_ONLY_PATH],
          // No runtimeCaching, by design: nothing under /api is ever cached, so a position, order or kill-switch
          // read is always live-or-absent, never stale (gh#650, R-19).
        },
        manifest: {
          name: 'Trading Co-Pilot',
          short_name: 'Co-Pilot',
          description: 'Single-operator futures trading co-pilot.',
          id: '/',
          start_url: '/',
          scope: '/',
          display: 'standalone',
          orientation: 'any',
          theme_color: '#101318',
          background_color: '#101318',
          icons: [
            { src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png', purpose: 'any' },
            { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png', purpose: 'any' },
            {
              src: '/icons/icon-maskable-512.png',
              sizes: '512x512',
              type: 'image/png',
              purpose: 'maskable',
            },
          ],
        },
      }),
    ],

    // Root-absolute asset URLs. The bundle is served by the BFF out of its wwwroot at "/", so
    // "/assets/…" resolves regardless of the deployed host.
    base: '/',

    build: {
      outDir: 'dist',
    },

    // Dev only. `npm run dev` serves the SPA from Vite's port, so a same-origin /health would hit
    // Vite rather than the BFF. There is deliberately no default target: a guessed host is a wrong
    // host, and an unset variable leaves the probe honestly reporting unreachable.
    server: bffOrigin
      ? { proxy: { '/health': { target: bffOrigin, changeOrigin: true } } }
      : undefined,

    test: {
      environment: 'jsdom',
      include: ['src/**/*.test.{ts,tsx}'],
      restoreMocks: true,

      // Vitest stubs CSS imports to empty by default, which also empties a `?raw` import of a
      // stylesheet. `tokens.test.ts` reads src/index.css that way to pin the one token value CSS
      // cannot import, so the stylesheet has to arrive with its contents intact.
      css: true,
    },
  };
});
