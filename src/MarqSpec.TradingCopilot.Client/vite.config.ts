import react from '@vitejs/plugin-react';
import { loadEnv } from 'vite';
import { defineConfig } from 'vitest/config';

export default defineConfig(({ mode }) => {
  // loadEnv folds matching process.env entries in with any .env file, so `VITE_BFF_ORIGIN=... npm run
  // dev` works without one. The '.' env dir keeps this off Node's `process` global, which would drag
  // @types/node into a browser-only project.
  const bffOrigin = loadEnv(mode, '.', 'VITE_').VITE_BFF_ORIGIN;

  return {
    plugins: [react()],

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
