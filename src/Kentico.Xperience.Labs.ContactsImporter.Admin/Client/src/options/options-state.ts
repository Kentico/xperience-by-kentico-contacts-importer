import type { ChoiceDto, FieldDto, ImportConfigResponse, JobDto, OptionsDto } from '../shared/commands';

/**
 * State of the Options step: the contact, recipient list and consent options of PLAN §8.
 * Pure functions; the React step only renders them.
 */

/**
 * Defaults of the PLAN §8 table. The recipient list and consent defaults are the ones that change
 * nothing: no list, unsubscribed contacts stay unsubscribed, no consent, revoked consents stay revoked.
 */
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
  recipientListId: null,
  unsubscribed: 'keep',
  bouncedAddress: 'addAndReport',
  consent: 'none',
  consentId: null,
  revokedConsent: 'keep',
};

/** What the options depend on: taken from the job (`GET_JOB`) and the configuration (`LOAD_CONFIG`). */
export interface OptionsContext extends Pick<JobDto, 'invalidEmailApplies' | 'sourceFields' | 'defaultSourceValue'> {
  /** Existing recipient lists. */
  readonly recipientLists: readonly ChoiceDto[];
  /** Existing consents. */
  readonly consents: readonly ChoiceDto[];
  /** A "consent given" column is mapped, so consent can be recorded per row. */
  readonly consentColumnMapped: boolean;
}

export function toOptionsContext(job: JobDto, config: Pick<ImportConfigResponse, 'recipientLists' | 'consents'>): OptionsContext {
  return {
    invalidEmailApplies: job.invalidEmailApplies,
    sourceFields: job.sourceFields,
    defaultSourceValue: job.defaultSourceValue,
    recipientLists: config.recipientLists ?? [],
    consents: config.consents ?? [],
    consentColumnMapped: !!job.consentMapping?.agreed,
  };
}

export type SourceValueProblem = 'missing' | 'tooLong';

export interface OptionsValidation {
  isValid: boolean;
  sourceValue: SourceValueProblem | null;
  /** Longest source value the chosen field can hold; `null` = no limit. */
  sourceValueMaxLength: number | null;
  /** New contacts are not created and existing ones not updated: the import would change no contact field. */
  nothingToDo: boolean;
  /** Consent agreements are to be recorded but no consent is chosen. */
  consentMissing: boolean;
}

/**
 * What the saved options no longer fit (the list or consent was deleted, the consent column is no
 * longer mapped). `initialOptions` replaced them with the option that changes nothing; the step says so.
 */
export interface DroppedOptions {
  recipientList: boolean;
  consent: boolean;
  consentColumn: boolean;
}

/** Options to edit: the stored ones, without choices that can no longer be used. */
export function initialOptions(saved: OptionsDto | null | undefined, context: OptionsContext): OptionsDto {
  let options: OptionsDto = { ...DEFAULT_OPTIONS, ...dropNulls(saved) };
  if (options.sourceField !== null && !findSourceField(options.sourceField, context)) {
    options = { ...options, sourceField: null, sourceValue: null, sourceStamp: DEFAULT_OPTIONS.sourceStamp };
  }

  const dropped = droppedOptions(saved, context);
  if (dropped.recipientList) {
    options = { ...options, recipientListId: null, unsubscribed: DEFAULT_OPTIONS.unsubscribed, bouncedAddress: DEFAULT_OPTIONS.bouncedAddress };
  }
  if (dropped.consent || dropped.consentColumn) {
    // Never fall back from "only rows that say yes" to "everyone": no consent is recorded until chosen again.
    options = { ...options, consent: 'none', consentId: null, revokedConsent: DEFAULT_OPTIONS.revokedConsent };
  }

  return options;
}

export function droppedOptions(saved: OptionsDto | null | undefined, context: OptionsContext): DroppedOptions {
  const listId = saved?.recipientListId ?? null;
  const consent = saved?.consent ?? 'none';
  const consentId = saved?.consentId ?? null;

  return {
    recipientList: listId !== null && !hasChoice(context.recipientLists, listId),
    consent: consent !== 'none' && consentId !== null && !hasChoice(context.consents, consentId),
    consentColumn: consent === 'blanketWithColumnOverride' && !context.consentColumnMapped,
  };
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

/**
 * Chooses the recipient list (`null` = recipient lists are not changed). Choosing another list
 * returns the options about unsubscribed contacts and bounced addresses to their defaults: "subscribe
 * again" is decided per list.
 */
export function setRecipientList(options: OptionsDto, listId: number | null, context: OptionsContext): OptionsDto {
  const id = listId !== null && hasChoice(context.recipientLists, listId) ? listId : null;
  if (id === options.recipientListId) {
    return options;
  }

  return { ...options, recipientListId: id, unsubscribed: DEFAULT_OPTIONS.unsubscribed, bouncedAddress: DEFAULT_OPTIONS.bouncedAddress };
}

/**
 * Chooses how consent agreements are recorded. "Column override" needs a mapped consent column.
 * Turning consent off forgets the consent and returns "revoked" to keep.
 */
export function setConsentMode(options: OptionsDto, mode: OptionsDto['consent'], context: OptionsContext): OptionsDto {
  if (mode === 'blanketWithColumnOverride' && !context.consentColumnMapped) {
    return options;
  }
  if (mode === 'none') {
    return { ...options, consent: 'none', consentId: null, revokedConsent: DEFAULT_OPTIONS.revokedConsent };
  }

  return { ...options, consent: mode };
}

/** Chooses the consent. Another consent returns "revoked" to keep: "agree again" is decided per consent. */
export function setConsent(options: OptionsDto, consentId: number | null, context: OptionsContext): OptionsDto {
  const id = consentId !== null && hasChoice(context.consents, consentId) ? consentId : null;
  if (id === options.consentId) {
    return options;
  }

  return { ...options, consentId: id, revokedConsent: DEFAULT_OPTIONS.revokedConsent };
}

/** The options add contacts to a recipient list or record consent (so running needs the consent confirmation). */
export function affectsConsent(options: OptionsDto): boolean {
  return options.recipientListId !== null || options.consent !== 'none';
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

  const saved = toSaveRequest(options, context);
  // Adding existing contacts to a list or recording their consent is work too.
  const nothingToDo = options.newContacts === 'skip' && options.existingContacts === 'skip' && !affectsConsent(saved);
  const consentMissing = saved.consent !== 'none' && saved.consentId === null;

  return {
    isValid: sourceValue === null && !nothingToDo && !consentMissing,
    sourceValue,
    sourceValueMaxLength: field?.maxLength ?? null,
    nothingToDo,
    consentMissing,
  };
}

/** Data of `SAVE_OPTIONS`: values that do not apply are sent as their defaults. */
export function toSaveRequest(options: OptionsDto, context: OptionsContext): OptionsDto {
  const hasSource = options.sourceField !== null && findSourceField(options.sourceField, context) !== undefined;
  const listId = options.recipientListId !== null && hasChoice(context.recipientLists, options.recipientListId) ? options.recipientListId : null;
  const consent = options.consent === 'blanketWithColumnOverride' && !context.consentColumnMapped ? 'none' : options.consent;
  const consentId = consent !== 'none' && options.consentId !== null && hasChoice(context.consents, options.consentId) ? options.consentId : null;

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
    recipientListId: listId,
    unsubscribed: listId !== null ? options.unsubscribed : DEFAULT_OPTIONS.unsubscribed,
    bouncedAddress: listId !== null ? options.bouncedAddress : DEFAULT_OPTIONS.bouncedAddress,
    consent,
    consentId,
    revokedConsent: consent !== 'none' ? options.revokedConsent : DEFAULT_OPTIONS.revokedConsent,
  };
}

/** Whether two option sets are the same to the server (what `SAVE_OPTIONS` would store). */
export function isSameOptions(a: OptionsDto, b: OptionsDto, context: OptionsContext): boolean {
  const x = toSaveRequest({ ...DEFAULT_OPTIONS, ...dropNulls(a) }, context);
  const y = toSaveRequest({ ...DEFAULT_OPTIONS, ...dropNulls(b) }, context);

  return (Object.keys(x) as (keyof OptionsDto)[]).every((key) => x[key] === y[key]);
}

/** Option names a server response does not hold (older job) fall back to the defaults instead of `undefined`. */
function dropNulls(options: OptionsDto | null | undefined): Partial<OptionsDto> {
  if (!options) {
    return {};
  }

  const nullable: ReadonlySet<string> = new Set(['sourceField', 'sourceValue', 'recipientListId', 'consentId']);

  return Object.fromEntries(Object.entries(options).filter(([key, value]) => value !== undefined && (value !== null || nullable.has(key)))) as Partial<OptionsDto>;
}

function hasChoice(choices: readonly ChoiceDto[], id: number): boolean {
  return choices.some((choice) => choice.id === id);
}

function findSourceField(name: string, context: OptionsContext): FieldDto | undefined {
  return context.sourceFields.find((field) => field.name === name);
}

function fitTo(value: string, field: FieldDto): string {
  return typeof field.maxLength === 'number' && value.length > field.maxLength ? value.slice(0, field.maxLength) : value;
}
