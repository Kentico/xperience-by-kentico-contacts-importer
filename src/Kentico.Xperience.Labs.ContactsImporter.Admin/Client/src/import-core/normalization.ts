import {
  DateFormat,
  EMAIL_FIELD,
  MAX_EMAIL_LENGTH,
  MAX_KEY_LENGTH,
} from './contract';

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export type KeyRejectReason = 'missingKey' | 'keyTooLong' | 'invalidEmailKey';

export type KeyResult =
  | { ok: true; key: string }
  | { ok: false; reason: KeyRejectReason };

/** Trims with `String.prototype.trim` semantics (the parity rule shared with the server). */
export function trimValue(value: string): string {
  return value.trim();
}

/** Trim + lowercase; the canonical form of an email address. */
export function normalizeEmail(value: string): string {
  return value.trim().toLowerCase();
}

/** Email validity per contract; expects an already normalized (trimmed) value. */
export function isValidEmail(value: string): boolean {
  return value.length <= MAX_EMAIL_LENGTH && EMAIL_PATTERN.test(value);
}

/** Normalizes a raw dedupe key value and says why it is unusable when it is. */
export function checkKey(keyField: string, raw: string): KeyResult {
  const isEmail = keyField === EMAIL_FIELD;
  const key = isEmail ? normalizeEmail(raw) : raw.trim();

  if (key.length === 0) {
    return { ok: false, reason: 'missingKey' };
  }
  if (key.length > MAX_KEY_LENGTH) {
    return { ok: false, reason: 'keyTooLong' };
  }
  if (isEmail && !isValidEmail(key)) {
    return { ok: false, reason: 'invalidEmailKey' };
  }

  return { ok: true, key };
}

/** Normalized dedupe key, or `null` when the key is invalid (row is skipped). */
export function normalizeKey(keyField: string, raw: string): string | null {
  const result = checkKey(keyField, raw);

  return result.ok ? result.key : null;
}

const ISO_DATE =
  /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:[.,](\d{1,7}))?)?\s*(Z|z|[+-]\d{2}(?::?\d{2})?)?)?$/;
const LOCAL_DATE =
  /^(\d{1,2})[./-](\d{1,2})[./-](\d{4})(?:[T ]+(\d{1,2}):(\d{2})(?::(\d{2}))?)?$/;

function pad(value: number, length = 2): string {
  return String(value).padStart(length, '0');
}

function daysInMonth(year: number, month: number): number {
  if (month === 2) {
    const leap = (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;

    return leap ? 29 : 28;
  }

  return month === 4 || month === 6 || month === 9 || month === 11 ? 30 : 31;
}

function isValidDate(year: number, month: number, day: number): boolean {
  return (
    year >= 1 &&
    month >= 1 &&
    month <= 12 &&
    day >= 1 &&
    day <= daysInMonth(year, month)
  );
}

function isValidTime(hour: number, minute: number, second: number): boolean {
  return hour <= 23 && minute <= 59 && second <= 59;
}

function normalizeOffset(offset: string): string {
  if (offset === 'Z' || offset === 'z') {
    return 'Z';
  }

  const digits = offset.slice(1).replace(':', '');
  const hours = digits.slice(0, 2);
  const minutes = digits.length > 2 ? digits.slice(2, 4) : '00';

  return `${offset[0]}${hours}:${minutes}`;
}

/**
 * Parses a trimmed date value into ISO 8601 (`YYYY-MM-DD`, or `YYYY-MM-DDTHH:mm:ss[.fff][offset]`
 * when the value has a time). ISO input is always accepted; `dmy` / `mdy` also accept
 * `D.M.YYYY`-style values with `.`, `/` or `-` separators. No time zone conversion is done.
 * Returns `null` when the value is not a valid date.
 */
export function parseDateToIso(value: string, format: DateFormat): string | null {
  const iso = ISO_DATE.exec(value);
  if (iso) {
    const year = Number(iso[1]);
    const month = Number(iso[2]);
    const day = Number(iso[3]);
    if (!isValidDate(year, month, day)) {
      return null;
    }

    const date = `${iso[1]}-${iso[2]}-${iso[3]}`;
    if (iso[4] === undefined) {
      return date;
    }

    const second = iso[6] ?? '00';
    if (!isValidTime(Number(iso[4]), Number(iso[5]), Number(second))) {
      return null;
    }

    const fraction = iso[7] !== undefined ? `.${iso[7]}` : '';
    const offset = iso[8] !== undefined ? normalizeOffset(iso[8]) : '';

    return `${date}T${iso[4]}:${iso[5]}:${second}${fraction}${offset}`;
  }

  if (format === 'iso') {
    return null;
  }

  const local = LOCAL_DATE.exec(value);
  if (!local) {
    return null;
  }

  const first = Number(local[1]);
  const second = Number(local[2]);
  const year = Number(local[3]);
  const day = format === 'dmy' ? first : second;
  const month = format === 'dmy' ? second : first;
  if (!isValidDate(year, month, day)) {
    return null;
  }

  const date = `${pad(year, 4)}-${pad(month)}-${pad(day)}`;
  if (local[4] === undefined) {
    return date;
  }

  const hour = Number(local[4]);
  const minute = Number(local[5]);
  const sec = Number(local[6] ?? '0');
  if (!isValidTime(hour, minute, sec)) {
    return null;
  }

  return `${date}T${pad(hour)}:${pad(minute)}:${pad(sec)}`;
}

/** Whether values of the Xperience data type are parsed as dates by the pipeline. */
export function isDateDataType(dataType: string | undefined): boolean {
  return dataType === 'date' || dataType === 'datetime';
}
