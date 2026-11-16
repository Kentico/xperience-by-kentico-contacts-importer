import { describe, expect, it } from 'vitest';

import type { TargetField } from '../import-core';

import {
  createMappingState,
  hasDateTarget,
  isSameMapping,
  mappedTargets,
  setColumnTarget,
  setDateFormat,
  setKeyField,
  toFieldMapping,
  validateMapping,
} from './mapping-state';
import type { SavedMapping } from './mapping-state';

const FIELDS: TargetField[] = [
  { name: 'ContactEmail', dataType: 'text', maxLength: 254 },
  { name: 'ContactFirstName', dataType: 'text', maxLength: 100 },
  { name: 'ContactLastName', dataType: 'text', maxLength: 100 },
  { name: 'ContactBirthday', dataType: 'datetime' },
  { name: 'CustomerID', dataType: 'text', maxLength: 50 },
];

const COLUMNS = ['Email', 'First Name', 'Customer', 'Born', 'Notes'];

const SUGGESTIONS = [
  { source: 'Email', target: 'ContactEmail' },
  { source: 'First Name', target: 'ContactFirstName' },
];

describe('createMappingState', () => {
  it('starts from the suggestions, with email as the key', () => {
    const state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS);

    expect(state.columns).toEqual([
      { source: 'Email', target: 'ContactEmail' },
      { source: 'First Name', target: 'ContactFirstName' },
      { source: 'Customer', target: '' },
      { source: 'Born', target: '' },
      { source: 'Notes', target: '' },
    ]);
    expect(state.keyField).toBe('ContactEmail');
    expect(state.dateFormat).toBe('iso');
  });

  it('prefers the saved mapping of the job over the suggestions', () => {
    const saved: SavedMapping = {
      keyField: 'CustomerID',
      mapping: [
        { source: 'Customer', target: 'CustomerID' },
        { source: 'Born', target: 'ContactBirthday' },
      ],
      dateFormat: 'dmy',
    };

    const state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS, saved);

    expect(toFieldMapping(state)).toEqual(saved.mapping);
    expect(state.keyField).toBe('CustomerID');
    expect(state.dateFormat).toBe('dmy');
    expect(isSameMapping(state, saved)).toBe(true);
  });

  it('drops targets that are not mappable fields and targets used twice', () => {
    const state = createMappingState(COLUMNS, FIELDS, [
      { source: 'Email', target: 'ContactEmail' },
      { source: 'Notes', target: 'ContactGUID' },
      { source: 'Customer', target: 'ContactEmail' },
    ]);

    expect(toFieldMapping(state)).toEqual([{ source: 'Email', target: 'ContactEmail' }]);
  });

  it('maps a repeated column name once', () => {
    const state = createMappingState(['Email', 'Email'], FIELDS, [{ source: 'Email', target: 'ContactEmail' }]);

    expect(state.columns.map((column) => column.target)).toEqual(['ContactEmail', '']);
  });

  it('has no key when nothing is mapped', () => {
    expect(createMappingState(COLUMNS, FIELDS, []).keyField).toBe('');
  });
});

describe('setColumnTarget', () => {
  it('moves a field from the column that had it', () => {
    const initial = createMappingState(COLUMNS, FIELDS, SUGGESTIONS);

    const state = setColumnTarget(initial, 4, 'ContactFirstName');

    expect(state.columns[1]).toEqual({ source: 'First Name', target: '' });
    expect(state.columns[4]).toEqual({ source: 'Notes', target: 'ContactFirstName' });
    expect(initial.columns[1].target).toBe('ContactFirstName');
  });

  it('several columns can be set to "do not import"', () => {
    let state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS);
    state = setColumnTarget(state, 0, '');
    state = setColumnTarget(state, 1, '');

    expect(mappedTargets(state)).toEqual([]);
    expect(state.keyField).toBe('');
  });

  it('falls back to email, then to the first mapped field, when the key column is unmapped', () => {
    let state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS);
    state = setColumnTarget(state, 2, 'CustomerID');
    state = setKeyField(state, 'CustomerID');
    expect(state.keyField).toBe('CustomerID');

    state = setColumnTarget(state, 2, '');
    expect(state.keyField).toBe('ContactEmail');

    state = setColumnTarget(state, 0, '');
    expect(state.keyField).toBe('ContactFirstName');
  });
});

describe('setKeyField / setDateFormat', () => {
  it('ignores a key that is not mapped', () => {
    const state = setKeyField(createMappingState(COLUMNS, FIELDS, SUGGESTIONS), 'CustomerID');

    expect(state.keyField).toBe('ContactEmail');
  });

  it('changes the date format', () => {
    expect(setDateFormat(createMappingState(COLUMNS, FIELDS, SUGGESTIONS), 'mdy').dateFormat).toBe('mdy');
  });
});

describe('hasDateTarget', () => {
  it('is true only when a date field is mapped', () => {
    let state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS);
    expect(hasDateTarget(state, FIELDS)).toBe(false);

    state = setColumnTarget(state, 3, 'ContactBirthday');
    expect(hasDateTarget(state, FIELDS)).toBe(true);
  });
});

describe('validateMapping', () => {
  it('accepts the suggested mapping', () => {
    expect(validateMapping(createMappingState(COLUMNS, FIELDS, SUGGESTIONS))).toEqual({
      problems: [],
      notices: [],
      missingColumns: [],
      isValid: true,
    });
  });

  it('requires a mapped column', () => {
    const validation = validateMapping(createMappingState(COLUMNS, FIELDS, []));

    expect(validation.problems).toEqual(['nothingMapped']);
    expect(validation.isValid).toBe(false);
  });

  it('requires a key', () => {
    const state = { ...createMappingState(COLUMNS, FIELDS, SUGGESTIONS), keyField: '' };

    expect(validateMapping(state).problems).toEqual(['noKey']);
  });

  it('notes a key other than email and a missing email mapping', () => {
    let state = createMappingState(COLUMNS, FIELDS, []);
    state = setColumnTarget(state, 2, 'CustomerID');

    const validation = validateMapping(state);

    expect(validation.isValid).toBe(true);
    expect(validation.notices).toEqual(['customKey', 'emailNotMapped']);
  });

  it('reports saved columns the selected file does not have', () => {
    const saved: SavedMapping = {
      keyField: 'ContactEmail',
      mapping: [
        { source: 'Email', target: 'ContactEmail' },
        { source: 'Surname', target: 'ContactLastName' },
      ],
      dateFormat: 'iso',
    };
    const state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS, saved);

    const validation = validateMapping(state, saved);

    expect(validation.problems).toEqual(['missingColumn']);
    expect(validation.missingColumns).toEqual(['Surname']);
    expect(isSameMapping(state, saved)).toBe(false);
  });
});

describe('isSameMapping', () => {
  const saved: SavedMapping = { keyField: 'ContactEmail', mapping: SUGGESTIONS, dateFormat: 'iso' };

  it('is false after any change that affects the uploaded data', () => {
    const state = createMappingState(COLUMNS, FIELDS, SUGGESTIONS, saved);

    expect(isSameMapping(state, saved)).toBe(true);
    expect(isSameMapping(setDateFormat(state, 'dmy'), saved)).toBe(false);
    expect(isSameMapping(setColumnTarget(state, 2, 'CustomerID'), saved)).toBe(false);
    expect(isSameMapping(setKeyField(state, 'ContactFirstName'), saved)).toBe(false);
  });
});
