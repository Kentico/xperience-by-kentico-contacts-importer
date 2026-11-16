import { EMAIL_FIELD, isDateDataType } from '../import-core';
import type { DateFormat, FieldMapping, TargetField } from '../import-core';

/**
 * State of the Map step: which file column goes to which contact field, the dedupe key and the
 * date format. Pure functions, so the rules are unit-tested without React.
 */
export interface MappingState {
  /** One entry per file column, in file order. `target` is empty for a column that is not imported. */
  readonly columns: readonly ColumnMapping[];
  /** Contact field used as the dedupe key; empty when nothing usable is mapped. */
  readonly keyField: string;
  readonly dateFormat: DateFormat;
}

export interface ColumnMapping {
  readonly source: string;
  readonly target: string;
}

export interface SavedMapping {
  keyField: string;
  mapping: readonly FieldMapping[];
  dateFormat: DateFormat;
}

export type MappingProblem =
  /** No column is mapped. */
  | 'nothingMapped'
  /** No dedupe key is chosen. */
  | 'noKey'
  /** A saved mapping refers to a column the file does not have. */
  | 'missingColumn';

export type MappingNotice =
  /** The key is not the email field: custom fields have no index, lookups can be slow. */
  | 'customKey'
  /** The email field is not mapped. */
  | 'emailNotMapped';

export interface MappingValidation {
  readonly problems: readonly MappingProblem[];
  readonly notices: readonly MappingNotice[];
  /** Saved source columns that are missing in the file (`missingColumn`). */
  readonly missingColumns: readonly string[];
  readonly isValid: boolean;
}

/**
 * Initial mapping for a file: the saved mapping of the job when there is one (resume, back from
 * upload), otherwise the suggestions of the profiler. Targets that are not mappable fields are dropped.
 */
export function createMappingState(
  columnNames: readonly string[],
  fields: readonly TargetField[],
  suggestions: readonly FieldMapping[],
  saved?: SavedMapping,
): MappingState {
  const known = new Set(fields.map((field) => field.name));
  const source = saved ? saved.mapping : suggestions;
  const targetBySource = new Map<string, string>();
  const used = new Set<string>();
  for (const { source: column, target } of source) {
    if (known.has(target) && !used.has(target) && !targetBySource.has(column)) {
      targetBySource.set(column, target);
      used.add(target);
    }
  }

  const seen = new Set<string>();
  const columns = columnNames.map((name) => {
    // A repeated column name can be mapped once: the parser reads the first one.
    const target = seen.has(name) ? '' : (targetBySource.get(name) ?? '');
    seen.add(name);

    return { source: name, target };
  });

  return {
    columns,
    keyField: pickKeyField(columns, saved?.keyField ?? ''),
    dateFormat: saved?.dateFormat ?? 'iso',
  };
}

/**
 * Maps the column at `columnIndex` to `target` (empty = do not import). A contact field can be
 * the target of one column only, so another column mapped to it is unmapped.
 */
export function setColumnTarget(state: MappingState, columnIndex: number, target: string): MappingState {
  const columns = state.columns.map((column, index) => {
    if (index === columnIndex) {
      return { ...column, target };
    }

    return target !== '' && column.target === target ? { ...column, target: '' } : column;
  });

  return { ...state, columns, keyField: pickKeyField(columns, state.keyField) };
}

export function setKeyField(state: MappingState, keyField: string): MappingState {
  return { ...state, keyField: pickKeyField(state.columns, keyField) };
}

export function setDateFormat(state: MappingState, dateFormat: DateFormat): MappingState {
  return { ...state, dateFormat };
}

/** Mapped targets in file order: the options of the key field select. */
export function mappedTargets(state: MappingState): string[] {
  return state.columns.filter((column) => column.target !== '').map((column) => column.target);
}

/** `job.json` mapping: mapped columns only, in file order. */
export function toFieldMapping(state: MappingState): FieldMapping[] {
  return state.columns
    .filter((column) => column.target !== '')
    .map((column) => ({ source: column.source, target: column.target }));
}

/** True when a mapped target is a date field, so the date format matters. */
export function hasDateTarget(state: MappingState, fields: readonly TargetField[]): boolean {
  const dateFields = new Set(fields.filter((field) => isDateDataType(field.dataType)).map((field) => field.name));

  return state.columns.some((column) => dateFields.has(column.target));
}

export function validateMapping(state: MappingState, saved?: SavedMapping): MappingValidation {
  const targets = mappedTargets(state);
  const problems: MappingProblem[] = [];
  const notices: MappingNotice[] = [];

  if (targets.length === 0) {
    problems.push('nothingMapped');
  } else if (state.keyField === '' || !targets.includes(state.keyField)) {
    problems.push('noKey');
  }

  const columnNames = new Set(state.columns.map((column) => column.source));
  const missingColumns = saved ? saved.mapping.map((m) => m.source).filter((name) => !columnNames.has(name)) : [];
  if (missingColumns.length > 0) {
    problems.push('missingColumn');
  }

  if (targets.length > 0) {
    if (state.keyField !== '' && state.keyField !== EMAIL_FIELD) {
      notices.push('customKey');
    }
    if (!targets.includes(EMAIL_FIELD)) {
      notices.push('emailNotMapped');
    }
  }

  return { problems, notices, missingColumns, isValid: problems.length === 0 };
}

/** Whether two mappings upload the same data (same file columns to the same fields, key and date format). */
export function isSameMapping(state: MappingState, saved: SavedMapping): boolean {
  const current = toFieldMapping(state);

  return (
    state.keyField === saved.keyField &&
    state.dateFormat === saved.dateFormat &&
    current.length === saved.mapping.length &&
    current.every((m, index) => m.source === saved.mapping[index].source && m.target === saved.mapping[index].target)
  );
}

/** Keeps the wanted key when it is mapped; otherwise email when mapped, else the first mapped field. */
function pickKeyField(columns: readonly ColumnMapping[], wanted: string): string {
  const targets = columns.filter((column) => column.target !== '').map((column) => column.target);
  if (wanted !== '' && targets.includes(wanted)) {
    return wanted;
  }
  if (targets.includes(EMAIL_FIELD)) {
    return EMAIL_FIELD;
  }

  return targets[0] ?? '';
}
