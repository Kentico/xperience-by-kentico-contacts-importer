// ESLint flat config. Kept to ESLint's own rules, typescript-eslint, and the Playwright plugin
// so the dependency tree stays small. Formatting is Prettier's job (npm run format).
import js from "@eslint/js";
import { defineConfig, globalIgnores } from "eslint/config";
import playwright from "eslint-plugin-playwright";
import tseslint from "typescript-eslint";

export default defineConfig(
  globalIgnores([
    "playwright/",
    "playwright-report/",
    "test-results/",
    "blob-report/",
  ]),
  {
    linterOptions: {
      reportUnusedDisableDirectives: "error",
      reportUnusedInlineConfigs: "error",
    },
  },
  {
    files: ["**/*.ts"],
    extends: [
      js.configs.recommended,
      tseslint.configs.strictTypeChecked,
      tseslint.configs.stylisticTypeChecked,
      playwright.configs["flat/recommended"],
    ],
    languageOptions: {
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
    rules: {
      eqeqeq: ["error", "always"],
      "object-shorthand": "error",
      "prefer-template": "error",
      "@typescript-eslint/consistent-type-imports": "error",
      "@typescript-eslint/no-import-type-side-effects": "error",
      "@typescript-eslint/switch-exhaustiveness-check": "error",
      // Numbers in messages, ids and generated file content are intended; strictTypeChecked forbids them
      "@typescript-eslint/restrict-template-expressions": [
        "error",
        { allowNumber: true },
      ],
      // Specs skip themselves when an optional environment (database, viewer user) is missing
      "playwright/no-skipped-test": ["error", { allowConditional: true }],
      // Page objects assert through expect* methods
      "playwright/expect-expect": [
        "error",
        { assertFunctionPatterns: ["^expect"] },
      ],
    },
  },
  // Setup projects prepare state (sign-in) and assert nothing themselves
  {
    files: ["**/*.setup.ts"],
    rules: {
      "playwright/expect-expect": "off",
    },
  },
  {
    files: ["*.mjs"],
    extends: [js.configs.recommended],
  },
);
