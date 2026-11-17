/**
 * Number, size, duration and date formatting in a given culture. The components pass the admin
 * UI culture (`useGlobalization().uiLocale`, see `useText` in `i18n.ts`); no React here.
 */

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'] as const;

/** `1536` -> `1.5 KB`. Binary units (1 KB = 1024 B), one decimal below 100. */
export function formatBytes(bytes: number, locale?: string): string {
  let value = Math.max(0, bytes);
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit++;
  }

  const digits = unit === 0 || value >= 100 ? 0 : 1;

  return `${value.toLocaleString(locale, { minimumFractionDigits: 0, maximumFractionDigits: digits })} ${UNITS[unit]}`;
}

export function formatNumber(value: number, locale?: string): string {
  return Math.round(value).toLocaleString(locale);
}

/** `42.4` -> `42`; below 10 one decimal (`3.5`), so slow starts still move. */
export function formatPercent(value: number, locale?: string): string {
  const digits = value < 10 && value > 0 ? 1 : 0;

  return value.toLocaleString(locale, { minimumFractionDigits: 0, maximumFractionDigits: digits });
}

interface DurationFormatLike {
  format(duration: { hours?: number; minutes?: number; seconds?: number }): string;
}

type DurationFormatConstructor = new (locale?: string, options?: { style: 'short' }) => DurationFormatLike;

/**
 * `75` -> `1 min, 15 sec`, `3700` -> `1 hr, 1 min` (en-US). Hours and minutes, or minutes and
 * seconds. Uses `Intl.DurationFormat` where the browser has it, otherwise the units of
 * `Intl.NumberFormat`.
 */
export function formatDuration(seconds: number, locale?: string): string {
  const total = Math.max(0, Math.round(seconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const rest = total % 60;
  const parts = hours > 0 ? { hours, minutes } : minutes > 0 ? { minutes, seconds: rest } : { seconds: rest };

  const DurationFormat = (Intl as unknown as { DurationFormat?: DurationFormatConstructor }).DurationFormat;
  if (DurationFormat) {
    return new DurationFormat(locale, { style: 'short' }).format(parts);
  }

  const unit = (value: number, name: 'hour' | 'minute' | 'second') =>
    new Intl.NumberFormat(locale, { style: 'unit', unit: name, unitDisplay: 'short' }).format(value);
  const units: string[] = [];
  if (parts.hours !== undefined) {
    units.push(unit(parts.hours, 'hour'));
  }
  if (parts.minutes !== undefined) {
    units.push(unit(parts.minutes, 'minute'));
  }
  if (parts.seconds !== undefined) {
    units.push(unit(parts.seconds, 'second'));
  }

  return units.join(' ');
}

/** ISO timestamp from the server (UTC) -> local date and time. */
export function formatDateTime(isoUtc: string, locale?: string): string {
  // The server serializes UTC times without a zone designator when their kind is unspecified.
  const hasZone = /(Z|[+-]\d{2}:?\d{2})$/i.test(isoUtc);
  const date = new Date(hasZone ? isoUtc : `${isoUtc}Z`);

  return Number.isNaN(date.getTime())
    ? isoUtc
    : date.toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'short' });
}

/** The formatters bound to one culture. */
export interface Formatters {
  readonly locale: string | undefined;
  number(value: number): string;
  bytes(bytes: number): string;
  percent(value: number): string;
  duration(seconds: number): string;
  dateTime(isoUtc: string): string;
}

export function createFormatters(locale: string | undefined): Formatters {
  return {
    locale,
    number: (value) => formatNumber(value, locale),
    bytes: (bytes) => formatBytes(bytes, locale),
    percent: (value) => formatPercent(value, locale),
    duration: (seconds) => formatDuration(seconds, locale),
    dateTime: (isoUtc) => formatDateTime(isoUtc, locale),
  };
}

/** Values for the placeholders of a resource string. */
export type TextParams = Readonly<Record<string, string | number>>;

/**
 * Formats the numbers among `params` in the culture, so a resource string never shows `1234567`
 * where the culture writes `1,234,567`. `count` stays a number: i18next picks the plural form
 * from it (the text shows the count through another, formatted placeholder).
 */
export function localizeParams(params: TextParams, formatters: Pick<Formatters, 'number'>): Record<string, string | number> {
  const result: Record<string, string | number> = {};
  for (const [name, value] of Object.entries(params)) {
    result[name] = typeof value === 'number' && name !== 'count' ? formatters.number(value) : value;
  }

  return result;
}
