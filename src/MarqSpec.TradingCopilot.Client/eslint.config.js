import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';

// Flat config (the only format ESLint 10 reads). `npm run lint` never writes -- it reports and exits
// non-zero, mirroring `dotnet format --verify-no-changes` on the .NET side of the repo.
export default tseslint.config(
  { ignores: ['dist/', 'coverage/'] },
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
);
