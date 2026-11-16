import { describe, expect, it } from 'vitest';

import type { OptionsDto } from '../shared/commands';

import { DEFAULT_OPTIONS, initialOptions, isSameOptions, setSourceField, toSaveRequest, validateOptions } from './options-state';
import type { OptionsContext } from './options-state';

const CONTEXT: OptionsContext = {
  invalidEmailApplies: true,
  sourceFields: [
    { name: 'ContactSource', caption: 'Source', dataType: 'text', maxLength: 20, isSupported: true, canBeKey: true },
    { name: 'ContactNotes', caption: 'Notes', dataType: 'longtext', maxLength: null, isSupported: true, canBeKey: true },
  ],
  defaultSourceValue: 'import-2026-11-16-newsletter-subscribers.csv',
};

describe('initialOptions', () => {
  it('uses the plan defaults for a job without stored options', () => {
    expect(initialOptions(null, CONTEXT)).toEqual(DEFAULT_OPTIONS);
    expect(DEFAULT_OPTIONS).toEqual({
      newContacts: 'create',
      existingContacts: 'update',
      emptyValues: 'ignore',
      multipleMatches: 'updateNewest',
      invalidEmail: 'skipRow',
      sourceField: null,
      sourceValue: null,
      sourceStamp: 'createdOnly',
      reportIncludesKey: false,
    });
  });

  it('keeps stored options and drops a source field that is no longer available', () => {
    const stored: OptionsDto = { ...DEFAULT_OPTIONS, emptyValues: 'clear', sourceField: 'ContactSource', sourceValue: 'x', sourceStamp: 'createdAndUpdated' };
    expect(initialOptions(stored, CONTEXT)).toEqual(stored);
    expect(initialOptions(stored, { ...CONTEXT, sourceFields: [] })).toEqual({
      ...stored,
      sourceField: null,
      sourceValue: null,
      sourceStamp: 'createdOnly',
    });
  });
});

describe('setSourceField', () => {
  it('fills the generated default value, cut to the field size', () => {
    const options = setSourceField(DEFAULT_OPTIONS, 'ContactSource', CONTEXT);
    expect(options.sourceField).toBe('ContactSource');
    expect(options.sourceValue).toBe('import-2026-11-16-ne');
    expect(setSourceField(DEFAULT_OPTIONS, 'ContactNotes', CONTEXT).sourceValue).toBe(CONTEXT.defaultSourceValue);
  });

  it('keeps a value the user typed and clears the field for no stamp or an unknown field', () => {
    const typed = { ...DEFAULT_OPTIONS, sourceValue: ' webinar ' };
    expect(setSourceField(typed, 'ContactSource', CONTEXT).sourceValue).toBe('webinar');
    expect(setSourceField({ ...typed, sourceField: 'ContactSource' }, null, CONTEXT).sourceField).toBeNull();
    expect(setSourceField(typed, 'ContactEmail', CONTEXT).sourceField).toBeNull();
  });
});

describe('validateOptions', () => {
  it('accepts the defaults', () => {
    expect(validateOptions(DEFAULT_OPTIONS, CONTEXT)).toEqual({ isValid: true, sourceValue: null, sourceValueMaxLength: null, nothingToDo: false });
  });

  it('requires a source value that fits the field', () => {
    const withField = { ...DEFAULT_OPTIONS, sourceField: 'ContactSource' };
    expect(validateOptions({ ...withField, sourceValue: '  ' }, CONTEXT)).toMatchObject({ isValid: false, sourceValue: 'missing', sourceValueMaxLength: 20 });
    expect(validateOptions({ ...withField, sourceValue: 'x'.repeat(21) }, CONTEXT)).toMatchObject({ isValid: false, sourceValue: 'tooLong' });
    expect(validateOptions({ ...withField, sourceValue: 'x'.repeat(20) }, CONTEXT).isValid).toBe(true);
    expect(validateOptions({ ...withField, sourceField: 'ContactNotes', sourceValue: 'x'.repeat(5000) }, CONTEXT).isValid).toBe(true);
  });

  it('refuses options that would import nothing', () => {
    expect(validateOptions({ ...DEFAULT_OPTIONS, newContacts: 'skip', existingContacts: 'skip' }, CONTEXT)).toMatchObject({
      isValid: false,
      nothingToDo: true,
    });
    expect(validateOptions({ ...DEFAULT_OPTIONS, newContacts: 'skip' }, CONTEXT).isValid).toBe(true);
  });
});

describe('toSaveRequest', () => {
  it('sends options that do not apply as their defaults', () => {
    const options: OptionsDto = {
      newContacts: 'create',
      existingContacts: 'skip',
      emptyValues: 'clear',
      multipleMatches: 'updateAll',
      invalidEmail: 'importWithoutEmail',
      sourceField: null,
      sourceValue: 'left over',
      sourceStamp: 'createdAndUpdated',
      reportIncludesKey: true,
    };

    expect(toSaveRequest(options, { ...CONTEXT, invalidEmailApplies: false })).toEqual({
      newContacts: 'create',
      existingContacts: 'skip',
      emptyValues: 'ignore',
      multipleMatches: 'updateNewest',
      invalidEmail: 'skipRow',
      sourceField: null,
      sourceValue: null,
      sourceStamp: 'createdOnly',
      reportIncludesKey: true,
    });
  });

  it('sends the chosen values and trims the source value', () => {
    const options: OptionsDto = {
      ...DEFAULT_OPTIONS,
      emptyValues: 'clear',
      multipleMatches: 'skip',
      invalidEmail: 'importWithoutEmail',
      sourceField: 'ContactSource',
      sourceValue: ' webinar ',
      sourceStamp: 'createdAndUpdated',
    };

    expect(toSaveRequest(options, CONTEXT)).toEqual({ ...options, sourceValue: 'webinar' });
  });
});

describe('isSameOptions', () => {
  it('compares what the server would store', () => {
    const a = { ...DEFAULT_OPTIONS, existingContacts: 'skip' as const, emptyValues: 'clear' as const };
    const b = { ...DEFAULT_OPTIONS, existingContacts: 'skip' as const };
    expect(isSameOptions(a, b, CONTEXT)).toBe(true);
    expect(isSameOptions(DEFAULT_OPTIONS, { ...DEFAULT_OPTIONS, emptyValues: 'clear' }, CONTEXT)).toBe(false);
    expect(isSameOptions(DEFAULT_OPTIONS, { ...DEFAULT_OPTIONS, reportIncludesKey: true }, CONTEXT)).toBe(false);
  });
});
