import { useGlobalization } from '@kentico/xperience-admin-base';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import { createFormatters, localizeParams } from './format';
import type { Formatters, TextParams } from './format';
import type { Message } from './text';

export interface TextApi extends Formatters {
  /**
   * The resource string `key` in the admin UI language, with `params` filled into its `{{name}}`
   * placeholders. Numbers are formatted in the admin UI culture; pass `count` for plural keys.
   */
  t(key: string, params?: TextParams): string;
  /** `t` for a `Message` returned by code without React. */
  m(message: Message): string;
}

/**
 * Texts and formatting of the admin UI culture. Texts come from the client resources the admin
 * gives to i18next (`useTranslation`); numbers, sizes, durations and dates use `uiLocale`.
 */
export function useText(): TextApi {
  const { t } = useTranslation();
  const { uiLocale } = useGlobalization();

  return useMemo(() => {
    const formatters = createFormatters(uiLocale);
    const text = (key: string, params?: TextParams): string =>
      params ? t(key, localizeParams(params, formatters)) : t(key);

    return { ...formatters, t: text, m: (message: Message) => text(message.key, message.params) };
  }, [t, uiLocale]);
}
