import { readFileSync } from 'node:fs';

import type { TextParams } from './format';
import type { Message } from './text';

/**
 * Test helpers: the client resource strings (en-US) as the admin gives them to i18next, and a
 * minimal `t` with the admin's i18next setup (`{{name}}` placeholders, no escaping, `_one` /
 * `_other` plural keys picked by `count`). Node only (reads the .resx file).
 */

export const CLIENT_RESX_PATH = new URL('../../../Resources/ContactsImporterClientResources.resx', import.meta.url);

/** Keys and texts of a .resx file. */
export function readResx(path: URL): Map<string, string> {
  const xml = readFileSync(path, 'utf8');
  const entries = new Map<string, string>();
  for (const match of xml.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g)) {
    entries.set(match[1], decodeXml(match[2]));
  }

  return entries;
}

function decodeXml(value: string): string {
  return value.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
}

let resources: Map<string, string> | undefined;

/** en-US text of `key` with `params` filled in, like `t` in the admin (numbers as given). */
export function enText(key: string, params: TextParams = {}): string {
  resources ??= readResx(CLIENT_RESX_PATH);
  const count = params.count;
  const pluralKey = typeof count === 'number' ? `${key}_${new Intl.PluralRules('en-US').select(count)}` : undefined;
  const text = (pluralKey && resources.get(pluralKey)) ?? resources.get(key);
  if (text === undefined) {
    throw new Error(`Missing resource ${key}`);
  }

  return text.replace(/\{\{\s*([\w$]+)\s*\}\}/g, (whole, name: string) => (params[name] === undefined ? whole : String(params[name])));
}

export function enMessage(message: Message): string {
  return enText(message.key, message.params);
}
