import { EMAIL_FIELD, INVALID_EMAIL_FLAG, MAX_RECORD_BYTES } from './contract';
import type { ChunkRecord, DateFormat, FieldMapping, TargetField } from './contract';
import { checkKey, isDateDataType, isValidEmail, parseDateToIso } from './normalization';
import type { KeyRejectReason } from './normalization';
import { ImportError } from './source';
import type { RowErrorCode, SourceRow } from './source';

/** Why a row was skipped client-side (never uploaded). */
export type RejectReason =
  | 'parseError'
  | KeyRejectReason
  | 'valueTooLong'
  | 'invalidDate'
  | 'recordTooLarge';

export interface RowMapperOptions {
  /** Target field used as the dedupe key; must be a mapping target. */
  keyField: string;
  /** Source column -> target field. Unmapped columns never leave the browser. */
  mapping: readonly FieldMapping[];
  /** Input format of date / datetime target fields. Default `iso`. */
  dateFormat?: DateFormat;
  /**
   * Schema of the target fields (from the server). Enables date parsing (`dataType`) and the
   * value length check (`maxLength`); when set, every mapping target must be listed.
   */
  fields?: readonly TargetField[];
}

export type MapResult =
  | { ok: true; record: ChunkRecord }
  | {
      ok: false;
      reason: RejectReason;
      /** Target field the reason applies to (`valueTooLong`, `invalidDate`). */
      field?: string;
      /** Parser error behind a `parseError`. */
      detail?: RowErrorCode;
    };

interface MappedColumn {
  source: string;
  target: string;
  index: number;
  isEmail: boolean;
  isDate: boolean;
  maxLength: number;
}

const FIELD_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;
const RECORD_ENCODER = new TextEncoder();

/**
 * Maps a source row to a chunk record: picks mapped columns, trims, lowercases the email,
 * converts dates to ISO, normalizes the dedupe key and applies the skip rules (D34).
 */
export class RowMapper {
  private readonly columns: readonly string[];
  private readonly keyField: string;
  private readonly dateFormat: DateFormat;
  private readonly mapped: MappedColumn[];
  private readonly emailIsKey: boolean;
  private readonly hasEmail: boolean;
  private readonly dynamicColumns: boolean;
  /** Upper bound of the JSON bytes of a record besides its key and values. */
  private readonly overheadBytes: number;
  private resolvedColumns = -1;

  /**
   * @param columns Column list of the source (live reference; may grow when `dynamicColumns`).
   * @param dynamicColumns When false, every mapped source column must already exist.
   */
  constructor(columns: readonly string[], options: RowMapperOptions, dynamicColumns = false) {
    const { keyField, mapping, fields } = options;
    if (mapping.length === 0) {
      throw new ImportError('invalidConfig', 'Mapping is empty.');
    }

    const schema = fields ? new Map(fields.map((field) => [field.name, field])) : undefined;
    const targets = new Set<string>();
    this.mapped = mapping.map(({ source, target }) => {
      if (!FIELD_NAME.test(target) || target === '__proto__') {
        throw new ImportError('invalidConfig', `'${target}' is not a valid target field name.`);
      }
      if (targets.has(target)) {
        throw new ImportError('invalidConfig', `Target field '${target}' is mapped more than once.`);
      }
      targets.add(target);

      const field = schema?.get(target);
      if (schema && !field) {
        throw new ImportError('invalidConfig', `Target field '${target}' is not a mappable field.`);
      }

      return {
        source,
        target,
        index: -1,
        isEmail: target === EMAIL_FIELD,
        isDate: isDateDataType(field?.dataType),
        maxLength: field?.maxLength && field.maxLength > 0 ? field.maxLength : 0,
      };
    });
    if (!targets.has(keyField)) {
      throw new ImportError('invalidConfig', `Key field '${keyField}' is not mapped.`);
    }

    this.columns = columns;
    this.keyField = keyField;
    this.dateFormat = options.dateFormat ?? 'iso';
    this.emailIsKey = keyField === EMAIL_FIELD;
    this.hasEmail = targets.has(EMAIL_FIELD);
    this.dynamicColumns = dynamicColumns;
    this.overheadBytes = 128 + this.mapped.reduce((sum, column) => sum + column.target.length + 6, 0);

    this.resolve();
    if (!dynamicColumns) {
      const missing = this.mapped.filter((column) => column.index === -1);
      if (missing.length > 0) {
        throw new ImportError(
          'invalidConfig',
          `Mapped source columns not found in the file: ${missing.map((c) => c.source).join(', ')}.`,
        );
      }
    }
  }

  map(row: SourceRow): MapResult {
    if (row.error) {
      return { ok: false, reason: 'parseError', detail: row.error };
    }
    if (this.dynamicColumns && this.columns.length !== this.resolvedColumns) {
      this.resolve();
    }

    const values = row.values;
    const v: Record<string, string> = {};
    let problem: { reason: RejectReason; field: string } | undefined;
    let chars = 0;
    for (const column of this.mapped) {
      const raw = column.index === -1 ? undefined : values[column.index];
      let value = raw === undefined ? '' : raw.trim();
      if (value.length > 0) {
        if (column.isEmail) {
          value = value.toLowerCase();
        } else if (column.isDate) {
          const iso = parseDateToIso(value, this.dateFormat);
          if (iso === null) {
            problem ??= { reason: 'invalidDate', field: column.target };
          } else {
            value = iso;
          }
        }
        if (column.maxLength > 0 && value.length > column.maxLength) {
          problem ??= { reason: 'valueTooLong', field: column.target };
        }
      }
      v[column.target] = value;
      chars += value.length;
    }

    // an unusable key is reported before problems of other fields
    const key = checkKey(this.keyField, v[this.keyField]);
    if (!key.ok) {
      return { ok: false, reason: key.reason };
    }
    if (problem) {
      return { ok: false, reason: problem.reason, field: problem.field };
    }

    const record: ChunkRecord = { r: row.rowNumber, k: key.key, v };
    if (this.hasEmail && !this.emailIsKey) {
      const email = v[EMAIL_FIELD];
      if (email.length > 0 && !isValidEmail(email)) {
        record.f = [INVALID_EMAIL_FLAG];
      }
    }

    // Cheap bound first (a UTF-16 unit is at most 6 bytes of JSON); exact size only for huge rows.
    if (
      (chars + key.key.length) * 6 + this.overheadBytes > MAX_RECORD_BYTES &&
      RECORD_ENCODER.encode(JSON.stringify(record)).byteLength > MAX_RECORD_BYTES
    ) {
      return { ok: false, reason: 'recordTooLarge' };
    }

    return { ok: true, record };
  }

  private resolve(): void {
    const index = new Map<string, number>();
    this.columns.forEach((name, i) => {
      if (!index.has(name)) {
        index.set(name, i);
      }
    });
    for (const column of this.mapped) {
      column.index = index.get(column.source) ?? -1;
    }
    this.resolvedColumns = this.columns.length;
  }
}
