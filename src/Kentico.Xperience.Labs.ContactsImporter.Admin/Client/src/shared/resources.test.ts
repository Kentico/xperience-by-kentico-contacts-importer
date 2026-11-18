import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { CLIENT_RESX_PATH, readResx } from "./test-resources";

/**
 * The client texts live in `Resources/ContactsImporterClientResources.resx` and the code refers to
 * them by plain string keys (`t("contactsimporter.…")`), as the Xperience docs show. These checks
 * stand in for compile-time checking of the keys:
 * - every key the code names exists (a key used with `count` may exist as plural keys only),
 * - every key of the file is used,
 * - the placeholders of a text and the values passed where it is used are the same names.
 *
 * Keys are found as string literals in the source, so keys are never built from parts.
 */

const SRC = fileURLToPath(new URL("..", import.meta.url));
const PLURAL_SUFFIX = /_(zero|one|two|few|many|other)$/;
const KEY_LITERAL = /['"](contactsimporter\.[a-z0-9_.]+)['"]/g;

interface Site {
  readonly file: string;
  readonly key: string;
  /** Names passed for the placeholders; `null` when the values cannot be read (spread). */
  readonly params: ReadonlySet<string> | null;
}

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      return sourceFiles(path);
    }

    return /\.tsx?$/.test(entry.name) &&
      !/\.test\.ts$|test-resources\.ts$|test-helpers\.ts$|test-worker\.ts$/.test(
        entry.name,
      )
      ? [path]
      : [];
  });
}

/** Index after the bracket that closes the one at `open`; skips strings and comments. */
function closing(source: string, open: number): number {
  let depth = 0;
  for (let i = open; i < source.length; i++) {
    const char = source[i];
    if (char === "'" || char === '"' || char === "`") {
      for (i++; i < source.length && source[i] !== char; i++) {
        if (source[i] === "\\") {
          i++;
        }
      }
    } else if (char === "/" && source[i + 1] === "/") {
      i = source.indexOf("\n", i);
    } else if (char === "(" || char === "[" || char === "{") {
      depth++;
    } else if (char === ")" || char === "]" || char === "}") {
      depth--;
      if (depth === 0) {
        return i + 1;
      }
    }
  }
  throw new Error("Unbalanced brackets");
}

/** Splits at the commas that are not inside brackets or strings. */
function splitTopLevel(text: string): string[] {
  const parts: string[] = [];
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    const char = text[i];
    if (
      char === "'" ||
      char === '"' ||
      char === "`" ||
      char === "(" ||
      char === "[" ||
      char === "{"
    ) {
      i =
        char === "(" || char === "[" || char === "{"
          ? closing(text, i) - 1
          : skipString(text, i);
    } else if (char === ",") {
      parts.push(text.slice(start, i));
      start = i + 1;
    }
  }
  parts.push(text.slice(start));

  return parts.map((part) => part.trim()).filter((part) => part !== "");
}

function skipString(text: string, start: number): number {
  let i = start + 1;
  for (; i < text.length && text[i] !== text[start]; i++) {
    if (text[i] === "\\") {
      i++;
    }
  }

  return i;
}

/** Property names of an object literal `{ a, b: 1 }`; `null` when it spreads another object. */
function propertyNames(objectLiteral: string): Set<string> | null {
  const names = new Set<string>();
  for (const part of splitTopLevel(objectLiteral.trim().slice(1, -1))) {
    if (part.startsWith("...")) {
      return null;
    }
    const name = /^['"]?([\w$]+)/.exec(part)?.[1];
    if (name === undefined) {
      throw new Error(`Unexpected property ${part}`);
    }
    names.add(name);
  }

  return names;
}

function keysIn(text: string): string[] {
  return [...text.matchAll(KEY_LITERAL)].map((match) => match[1]);
}

/** Calls `t(key, { … })` and messages `{ key: …, params: { … } }`, where the values can be compared. */
function sites(file: string, source: string): Site[] {
  const found: Site[] = [];
  for (const match of source.matchAll(/\bt\(/g)) {
    const open = match.index + 1;
    const args = splitTopLevel(
      source.slice(open + 1, closing(source, open) - 1),
    );
    const params = args[1]?.startsWith("{")
      ? propertyNames(args[1])
      : new Set<string>();
    for (const key of keysIn(args[0] ?? "")) {
      found.push({ file, key, params });
    }
  }
  for (const match of source.matchAll(/\{\s*key:/g)) {
    const object = source.slice(match.index, closing(source, match.index));
    const parts = splitTopLevel(object.slice(1, -1));
    const keyPart = parts.find((part) => part.startsWith("key:"));
    const paramsPart = parts.find((part) => part.startsWith("params:"));
    const params = paramsPart
      ? propertyNames(paramsPart.slice("params:".length))
      : new Set<string>();
    for (const key of keysIn(keyPart ?? "")) {
      found.push({ file, key, params });
    }
  }

  return found;
}

function placeholders(text: string): Set<string> {
  return new Set(
    [...text.matchAll(/\{\{\s*([\w$]+)\s*\}\}/g)].map((match) => match[1]),
  );
}

const resources = readResx(CLIENT_RESX_PATH);
const files = sourceFiles(SRC).map((file) => ({
  file: file.slice(SRC.length),
  source: readFileSync(file, "utf8"),
}));
const usedKeys = new Map<string, string>();
for (const { file, source } of files) {
  for (const key of keysIn(source)) {
    usedKeys.set(key, file);
  }
}
const allSites = files.flatMap(({ file, source }) => sites(file, source));

/** The resource keys behind a key the code uses: itself, or its plural forms. */
function resourceKeys(key: string): string[] {
  if (resources.has(key)) {
    return [key];
  }

  return [...resources.keys()].filter(
    (candidate) =>
      candidate.replace(PLURAL_SUFFIX, "") === key &&
      PLURAL_SUFFIX.test(candidate),
  );
}

describe("client resources", () => {
  it("has a text for every key the client uses", () => {
    const missing = [...usedKeys]
      .filter(([key]) => resourceKeys(key).length === 0)
      .map(([key, file]) => `${key} (${file})`);

    expect(missing).toEqual([]);
  });

  it("uses every key of the resource file", () => {
    const orphans = [...resources.keys()].filter(
      (key) =>
        !usedKeys.has(key) && !usedKeys.has(key.replace(PLURAL_SUFFIX, "")),
    );

    expect(orphans).toEqual([]);
  });

  it("never builds keys from parts", () => {
    const built = files
      .filter(({ source }) =>
        /[`'"]contactsimporter\.[a-z0-9_.]*(\$\{|['"]\s*\+)/.test(source),
      )
      .map(({ file }) => file);

    expect(built).toEqual([]);
  });

  it("passes the placeholders each text has, and only those", () => {
    const problems: string[] = [];
    for (const site of allSites) {
      if (site.params === null) {
        continue;
      }
      const keys = resourceKeys(site.key);
      const expected = new Set(
        keys.flatMap((key) => [...placeholders(resources.get(key) ?? "")]),
      );
      const plural = keys.some((key) => PLURAL_SUFFIX.test(key));
      const passed = new Set(
        [...site.params].filter((name) => !(plural && name === "count")),
      );
      if (plural && !site.params.has("count")) {
        problems.push(`${site.key} (${site.file}): plural text without count`);
      }
      if ([...expected].sort().join() !== [...passed].sort().join()) {
        problems.push(
          `${site.key} (${site.file}): text has {${[...expected].join(", ")}}, call passes {${[...passed].join(", ")}}`,
        );
      }
    }

    expect(problems).toEqual([]);
  });

  it("passes values where it uses a text with placeholders", () => {
    const checked = new Set(allSites.map((site) => site.key));
    const unchecked = [...usedKeys.keys()].filter(
      (key) =>
        !checked.has(key) &&
        resourceKeys(key).some(
          (resourceKey) =>
            placeholders(resources.get(resourceKey) ?? "").size > 0,
        ),
    );

    expect(unchecked).toEqual([]);
  });

  it("uses the i18next placeholder syntax only", () => {
    const numbered = [...resources]
      .filter(([, text]) => /\{\d+\}/.test(text))
      .map(([key]) => key);

    expect(numbered).toEqual([]);
  });
});
