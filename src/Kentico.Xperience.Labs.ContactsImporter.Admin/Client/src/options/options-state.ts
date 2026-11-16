import type { FieldDto, JobDto, OptionsDto } from '../shared/commands';

/**
 * State of the Options step: the contact options of PLAN §8 (recipient list and consent options
 * come with a later version). Pure functions; the React step only renders them.
 */

/** Defaults of the PLAN §8 table. */
export const DEFAULT_OPTIONS: OptionsDto = {
  newContacts: 'create',
  existingContacts: 'update',
  emptyValues: 'ignore',
  multipleMatches: 'updateNewest',
  invalidEmail: 'skipRow',
  sourceField: null,
  sourceValue: null,
  sourceStamp: 'createdOnly',
  reportIncludesKey: false,
};

/** What the options depend on: taken from the job (`GET_JOB`). */
export type OptionsContext = Pick<JobDto, 'invalidEmailApplies' | 'sourceFields' | 'defaultSourceValue'>;

export type SourceValueProblem = 'missing' | 'tooLong';

export interface OptionsValidation {
  isValid: boolean;
  sourceValue: SourceValueProblem | null;
  /** Longest source value the chosen field can hold; `null` = no limit. */
  sourceValueMaxLength: number | null;
  /** New contacts are not created and existing ones not updated: the import would change nothing. */
  nothingToDo: boolean;
}

/** Options to edit: the stored ones, without a source field that can no longer be used. */
export function initialOptions(saved: OptionsDto | null | undefined, context: OptionsContext): OptionsDto {
  const options = { ...DEFAULT_OPTIONS, ...saved };
  if (options.sourceField !== null && !findSourceField(options.sourceField, context)) {
    return { ...options, sourceField: null, sourceValue: null, sourceStamp: DEFAULT_OPTIONS.sourceStamp };
  }

  return options;
}

/**
 * Chooses the source field (`null` = no stamp). An empty value is filled with the generated
 * default, a value too long for the new field is cut.
 */
export function setSourceField(options: OptionsDto, fieldName: string | null, context: OptionsContext): OptionsDto {
  const field = fieldName === null ? undefined : findSourceField(fieldName, context);
  if (!field) {
    return { ...options, sourceField: null };
  }

  const current = options.sourceValue?.trim() ?? '';
  const value = current === '' ? context.defaultSourceValue : current;

  return { ...options, sourceField: field.name, sourceValue: fitTo(value, field) };
}

export function validateOptions(options: OptionsDto, context: OptionsContext): OptionsValidation {
  const field = options.sourceField === null ? undefined : findSourceField(options.sourceField, context);
  const value = options.sourceValue?.trim() ?? '';
  let sourceValue: SourceValueProblem | null = null;
  if (field && value === '') {
    sourceValue = 'missing';
  } else if (field && typeof field.maxLength === 'number' && value.length > field.maxLength) {
    sourceValue = 'tooLong';
  }

  const nothingToDo = options.newContacts === 'skip' && options.existingContacts === 'skip';

  return {
    isValid: sourceValue === null && !nothingToDo,
    sourceValue,
    sourceValueMaxLength: field?.maxLength ?? null,
    nothingToDo,
  };
}

/** Data of `SAVE_OPTIONS`: values that do not apply are sent as their defaults. */
export function toSaveRequest(options: OptionsDto, context: OptionsContext): OptionsDto {
  const hasSource = options.sourceField !== null && findSourceField(options.sourceField, context) !== undefined;

  return {
    newContacts: options.newContacts,
    existingContacts: options.existingContacts,
    emptyValues: options.existingContacts === 'update' ? options.emptyValues : DEFAULT_OPTIONS.emptyValues,
    multipleMatches: options.existingContacts === 'update' ? options.multipleMatches : DEFAULT_OPTIONS.multipleMatches,
    invalidEmail: context.invalidEmailApplies ? options.invalidEmail : DEFAULT_OPTIONS.invalidEmail,
    sourceField: hasSource ? options.sourceField : null,
    sourceValue: hasSource ? (options.sourceValue?.trim() ?? '') : null,
    sourceStamp: hasSource ? options.sourceStamp : DEFAULT_OPTIONS.sourceStamp,
    reportIncludesKey: options.reportIncludesKey,
  };
}

/** Whether two option sets are the same to the server (what `SAVE_OPTIONS` would store). */
export function isSameOptions(a: OptionsDto, b: OptionsDto, context: OptionsContext): boolean {
  const x = toSaveRequest(a, context);
  const y = toSaveRequest(b, context);

  return (Object.keys(x) as (keyof OptionsDto)[]).every((key) => x[key] === y[key]);
}

function findSourceField(name: string, context: OptionsContext): FieldDto | undefined {
  return context.sourceFields.find((field) => field.name === name);
}

function fitTo(value: string, field: FieldDto): string {
  return typeof field.maxLength === 'number' && value.length > field.maxLength ? value.slice(0, field.maxLength) : value;
}
