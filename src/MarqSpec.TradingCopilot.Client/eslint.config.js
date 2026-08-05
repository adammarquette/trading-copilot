import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';

// Shared by the fetch-ban rules below (R-18): the message is the same whichever shape the bypass takes.
const ROUTE_THROUGH_CLIENT =
  'Route BFF calls through api/client.ts — the one place the JWT is attached (R-18). Add a genuinely-anonymous raw request to this allow-list so the exception stays on the record.';

// Flat config (the only format ESLint 10 reads). `npm run lint` never writes -- it reports and exits
// non-zero, mirroring `dotnet format --verify-no-changes` on the .NET side of the repo.
export default tseslint.config(
  // src/api/schema.ts is generated from openapi/v1.json (`npm run codegen`); it is not hand-edited and
  // openapi-typescript owns its style, so it is excluded from lint the same way dist/ output is.
  { ignores: ['dist/', 'coverage/', 'src/api/schema.ts'] },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      ...tseslint.configs.recommended,
      // `.flat.` matters: the plugin still ships eslintrc-shaped configs under the same names, and
      // ESLint 10 rejects those outright.
      reactHooks.configs.flat.recommended,

      // Last on purpose: turns off every rule Prettier already decides, so the two tools cannot
      // disagree about the same line.
      prettier,
    ],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'module',
      globals: globals.browser,
    },
  },
  {
    // R-18 / gh#648: authenticated BFF calls attach the JWT from exactly one place — src/api/client.ts. So any
    // other module reaching the global `fetch` is almost certainly an unauthenticated call that skipped the
    // bearer, and is banned here rather than left to a doc comment (gh#685; PR #677/#686 review — #648's
    // acceptance asked that a bypassing call "does not compile or is caught by a test"). It takes two rules
    // because the bypass has two shapes: a bare `fetch(...)` identifier, and the member forms
    // `window`/`globalThis`/`self` `.fetch(...)` that resolve to the same global and would slip past the first.
    // The one sanctioned exception is the anonymous `/health` probe, which wants a raw request rather than an
    // `ApiResult`; adding another is a deliberate, reviewable edit to this allow-list.
    files: ['src/**/*.{ts,tsx}'],
    ignores: ['src/api/client.ts', 'src/health/useBffHealth.ts'],
    rules: {
      'no-restricted-globals': ['error', { name: 'fetch', message: ROUTE_THROUGH_CLIENT }],
      'no-restricted-properties': [
        'error',
        { object: 'window', property: 'fetch', message: ROUTE_THROUGH_CLIENT },
        { object: 'globalThis', property: 'fetch', message: ROUTE_THROUGH_CLIENT },
        { object: 'self', property: 'fetch', message: ROUTE_THROUGH_CLIENT },
      ],
    },
  },
);
