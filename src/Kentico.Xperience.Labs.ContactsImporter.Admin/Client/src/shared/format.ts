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

/** `75` -> `1 min 15 s`, `3700` -> `1 h 2 min`, `8` -> `8 s`. */
export function formatDuration(seconds: number): string {
  const total = Math.max(0, Math.round(seconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const rest = total % 60;

  if (hours > 0) {
    return `${hours} h ${minutes} min`;
  }
  if (minutes > 0) {
    return `${minutes} min ${rest} s`;
  }

  return `${rest} s`;
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
