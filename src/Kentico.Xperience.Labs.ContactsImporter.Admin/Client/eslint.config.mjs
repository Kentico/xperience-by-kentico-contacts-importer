// ESLint flat config. Kept to ESLint's own rules, typescript-eslint, and the React team's hooks plugin
// so the dependency tree stays small. eslint-plugin-react is left out: it does not support ESLint 10.
import js from "@eslint/js";
import { defineConfig, globalIgnores } from "eslint/config";
import reactHooks from "eslint-plugin-react-hooks";
import globals from "globals";
import tseslint from "typescript-eslint";

export default defineConfig(
  globalIgnores(["dist/", "node_modules/"]),
  {
    linterOptions: {
      reportUnusedDisableDirectives: "error",
      reportUnusedInlineConfigs: "error",
    },
  },
  // App code, unit tests, and vitest.config.mts; tsconfig.json covers every TS file in the folder
  {
    files: ["src/**/*.{ts,tsx}", "*.mts"],
    extends: [
      js.configs.recommended,
      tseslint.configs.strictTypeChecked,
      tseslint.configs.stylisticTypeChecked,
      reactHooks.configs.flat.recommended,
    ],
    languageOptions: {
      globals: globals.browser,
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
    rules: {
      eqeqeq: ["error", "always"],
      "no-console": ["error", { allow: ["warn", "error"] }],
      "object-shorthand": "error",
      "prefer-template": "error",
      "@typescript-eslint/consistent-type-imports": "error",
      "@typescript-eslint/no-import-type-side-effects": "error",
      // A `default` branch is an explicit choice to handle the remaining cases together
      "@typescript-eslint/switch-exhaustiveness-check": [
        "error",
        { considerDefaultExhaustiveForUnions: true },
      ],
      // `||` on strings is used on purpose to treat "" like a missing value
      "@typescript-eslint/prefer-nullish-coalescing": [
        "error",
        { ignorePrimitives: { string: true } },
      ],
      // Numbers in template literals (sizes, counts, offsets in messages) are intended; the strict
      // preset's default flags each one
      "@typescript-eslint/restrict-template-expressions": [
        "error",
        { allowNumber: true },
      ],
      // Destructuring with a rest element is the way to leave properties out of an object
      "@typescript-eslint/no-unused-vars": [
        "error",
        { ignoreRestSiblings: true },
      ],
      // Every hook dependency missing from the list is a stale-closure bug
      "react-hooks/exhaustive-deps": "error",
    },
  },
  // Web Worker entry (bundled by build/inline-worker-loader.js)
  {
    files: ["src/worker/import-worker.ts"],
    languageOptions: {
      globals: globals.worker,
    },
  },
  // Build tooling (webpack config and loader; CommonJS, runs in Node)
  {
    files: ["*.{js,mjs}", "build/**/*.js"],
    extends: [js.configs.recommended],
    languageOptions: {
      globals: globals.node,
    },
  },
  {
    files: ["*.js", "build/**/*.js"],
    languageOptions: {
      sourceType: "commonjs",
    },
  },
);
