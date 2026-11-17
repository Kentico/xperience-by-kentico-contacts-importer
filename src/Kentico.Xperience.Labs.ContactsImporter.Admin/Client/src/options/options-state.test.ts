import { describe, expect, it } from 'vitest';

import type { OptionsDto } from '../shared/commands';

import {
  DEFAULT_OPTIONS,
  affectsConsent,
  droppedOptions,
  initialOptions,
  isSameOptions,
  setConsent,
  setConsentMode,
  setRecipientList,
  setSourceField,
  toOptionsContext,
  toSaveRequest,
  validateOptions,
} from './options-state';
import type { OptionsContext } from './options-state';

const CONTEXT: OptionsContext = {
  invalidEmailApplies: true,
  sourceFields: [
    { name: 'ContactSource', caption: 'Source', dataType: 'text', maxLength: 20, isSupported: true, canBeKey: true },
    { name: 'ContactNotes', caption: 'Notes', dataType: 'longtext', maxLength: null, isSupported: true, canBeKey: true },
  ],
  defaultSourceValue: 'import-2026-11-16-newsletter-subscribers.csv',
  recipientLists: [
    { id: 11, name: 'Newsletter' },
    { id: 12, name: 'Product news' },
  ],
  consents: [
    { id: 21, name: 'Newsletter signup 2025' },
    { id: 22, name: 'Webinar signup' },
  ],
  consentColumnMapped: true,
};

const NO_LIST_OR_CONSENT = {
  recipientListId: null,
  unsubscribed: 'keep',
  bouncedAddress: 'addAndReport',
  consent: 'none',
  consentId: null,
  revokedConsent: 'keep',
} as const;

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
      // the safe defaults: no list, keep unsubscribed, no consent, keep revoked
      recipientListId: null,
      unsubscribed: 'keep',
      bouncedAddress: 'addAndReport',
      consent: 'none',
      consentId: null,
      revokedConsent: 'keep',
    });
  });

  it('fills options an older job does not have with the defaults', () => {
    const { recipientListId, unsubscribed, bouncedAddress, consent, consentId, revokedConsent, ...old } = DEFAULT_OPTIONS;
    void [recipientListId, unsubscribed, bouncedAddress, consent, consentId, revokedConsent];

    expect(initialOptions({ ...old, emptyValues: 'clear' } as OptionsDto, CONTEXT)).toEqual({ ...DEFAULT_OPTIONS, emptyValues: 'clear' });
    expect(initialOptions({ ...DEFAULT_OPTIONS, unsubscribed: null, consent: null } as unknown as OptionsDto, CONTEXT)).toEqual(DEFAULT_OPTIONS);
  });

  it('keeps a stored list and consent that still exist', () => {
    const stored: OptionsDto = {
      ...DEFAULT_OPTIONS,
      recipientListId: 12,
      unsubscribed: 'resubscribe',
      bouncedAddress: 'skipAndReport',
      consent: 'blanketWithColumnOverride',
      consentId: 22,
      revokedConsent: 'reAgree',
    };

    expect(initialOptions(stored, CONTEXT)).toEqual(stored);
    expect(droppedOptions(stored, CONTEXT)).toEqual({ recipientList: false, consent: false, consentColumn: false });
  });

  it('drops a list that no longer exists, with its resubscribe choice', () => {
    const stored: OptionsDto = { ...DEFAULT_OPTIONS, recipientListId: 99, unsubscribed: 'resubscribe', bouncedAddress: 'skipAndReport' };

    expect(initialOptions(stored, CONTEXT)).toEqual(DEFAULT_OPTIONS);
    expect(droppedOptions(stored, CONTEXT)).toMatchObject({ recipientList: true });
  });

  it('drops a consent that no longer exists and never widens "only rows that say yes" to everyone', () => {
    const gone: OptionsDto = { ...DEFAULT_OPTIONS, consent: 'blanket', consentId: 99, revokedConsent: 'reAgree' };
    expect(initialOptions(gone, CONTEXT)).toEqual(DEFAULT_OPTIONS);
    expect(droppedOptions(gone, CONTEXT)).toMatchObject({ consent: true, consentColumn: false });

    const noColumn: OptionsDto = { ...DEFAULT_OPTIONS, consent: 'blanketWithColumnOverride', consentId: 21, revokedConsent: 'reAgree' };
    const context = { ...CONTEXT, consentColumnMapped: false };
    expect(initialOptions(noColumn, context)).toEqual(DEFAULT_OPTIONS);
    expect(droppedOptions(noColumn, context)).toMatchObject({ consent: false, consentColumn: true });
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
    expect(validateOptions(DEFAULT_OPTIONS, CONTEXT)).toEqual({
      isValid: true,
      sourceValue: null,
      sourceValueMaxLength: null,
      nothingToDo: false,
      consentMissing: false,
    });
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

  it('accepts options that only add existing contacts to a list or record their consent', () => {
    const skipBoth = { ...DEFAULT_OPTIONS, newContacts: 'skip' as const, existingContacts: 'skip' as const };

    expect(validateOptions({ ...skipBoth, recipientListId: 11 }, CONTEXT)).toMatchObject({ isValid: true, nothingToDo: false });
    expect(validateOptions({ ...skipBoth, consent: 'blanket', consentId: 21 }, CONTEXT)).toMatchObject({ isValid: true, nothingToDo: false });
    // a list that does not exist is not sent, so nothing would happen
    expect(validateOptions({ ...skipBoth, recipientListId: 99 }, CONTEXT)).toMatchObject({ isValid: false, nothingToDo: true });
  });

  it('requires a consent when consent agreements are recorded', () => {
    expect(validateOptions({ ...DEFAULT_OPTIONS, consent: 'blanket' }, CONTEXT)).toMatchObject({ isValid: false, consentMissing: true });
    expect(validateOptions({ ...DEFAULT_OPTIONS, consent: 'blanket', consentId: 99 }, CONTEXT)).toMatchObject({ isValid: false, consentMissing: true });
    expect(validateOptions({ ...DEFAULT_OPTIONS, consent: 'blanketWithColumnOverride', consentId: 21 }, CONTEXT).isValid).toBe(true);
    expect(validateOptions({ ...DEFAULT_OPTIONS, consent: 'none', consentId: null }, CONTEXT).isValid).toBe(true);
  });
});

describe('setRecipientList', () => {
  it('chooses an existing list and nothing else', () => {
    expect(setRecipientList(DEFAULT_OPTIONS, 11, CONTEXT)).toEqual({ ...DEFAULT_OPTIONS, recipientListId: 11 });
    expect(setRecipientList(DEFAULT_OPTIONS, 99, CONTEXT)).toEqual(DEFAULT_OPTIONS);
  });

  it('returns "subscribe again" and the bounce option to their defaults when the list changes', () => {
    const chosen: OptionsDto = { ...DEFAULT_OPTIONS, recipientListId: 11, unsubscribed: 'resubscribe', bouncedAddress: 'skipAndReport' };

    expect(setRecipientList(chosen, 12, CONTEXT)).toEqual({ ...DEFAULT_OPTIONS, recipientListId: 12 });
    expect(setRecipientList(chosen, null, CONTEXT)).toEqual(DEFAULT_OPTIONS);
    expect(setRecipientList(chosen, 11, CONTEXT)).toBe(chosen);
  });
});

describe('setConsentMode / setConsent', () => {
  it('offers the column mode only with a mapped consent column', () => {
    expect(setConsentMode(DEFAULT_OPTIONS, 'blanketWithColumnOverride', CONTEXT).consent).toBe('blanketWithColumnOverride');
    expect(setConsentMode(DEFAULT_OPTIONS, 'blanketWithColumnOverride', { ...CONTEXT, consentColumnMapped: false })).toBe(DEFAULT_OPTIONS);
    expect(setConsentMode(DEFAULT_OPTIONS, 'blanket', { ...CONTEXT, consentColumnMapped: false }).consent).toBe('blanket');
  });

  it('turning consent off forgets the consent and "record a new agreement"', () => {
    const chosen: OptionsDto = { ...DEFAULT_OPTIONS, consent: 'blanket', consentId: 21, revokedConsent: 'reAgree' };

    expect(setConsentMode(chosen, 'none', CONTEXT)).toEqual(DEFAULT_OPTIONS);
    expect(setConsentMode(chosen, 'blanketWithColumnOverride', CONTEXT)).toEqual({ ...chosen, consent: 'blanketWithColumnOverride' });
  });

  it('another consent returns "record a new agreement" to keep', () => {
    const chosen: OptionsDto = { ...DEFAULT_OPTIONS, consent: 'blanket', consentId: 21, revokedConsent: 'reAgree' };

    expect(setConsent(chosen, 22, CONTEXT)).toEqual({ ...chosen, consentId: 22, revokedConsent: 'keep' });
    expect(setConsent(chosen, 21, CONTEXT)).toBe(chosen);
    expect(setConsent(chosen, 99, CONTEXT)).toEqual({ ...chosen, consentId: null, revokedConsent: 'keep' });
  });
});

describe('affectsConsent', () => {
  it('is true for a recipient list or a consent mode', () => {
    expect(affectsConsent(DEFAULT_OPTIONS)).toBe(false);
    expect(affectsConsent({ ...DEFAULT_OPTIONS, recipientListId: 11 })).toBe(true);
    expect(affectsConsent({ ...DEFAULT_OPTIONS, consent: 'blanket', consentId: 21 })).toBe(true);
    // options that only matter with a list or a consent
    expect(affectsConsent({ ...DEFAULT_OPTIONS, unsubscribed: 'resubscribe', revokedConsent: 'reAgree' })).toBe(false);
  });
});

describe('toOptionsContext', () => {
  it('takes the choices from the configuration and the consent column from the job', () => {
    const job = {
      invalidEmailApplies: false,
      sourceFields: [],
      defaultSourceValue: 'x',
      consentMapping: { agreed: 'Opt in', date: null },
    } as unknown as Parameters<typeof toOptionsContext>[0];

    expect(toOptionsContext(job, { recipientLists: CONTEXT.recipientLists as never, consents: [] })).toEqual({
      invalidEmailApplies: false,
      sourceFields: [],
      defaultSourceValue: 'x',
      recipientLists: CONTEXT.recipientLists,
      consents: [],
      consentColumnMapped: true,
    });
    expect(toOptionsContext({ ...job, consentMapping: null }, { recipientLists: [], consents: [] }).consentColumnMapped).toBe(false);
    expect(toOptionsContext({ ...job, consentMapping: { date: 'When' } }, { recipientLists: [], consents: [] }).consentColumnMapped).toBe(false);
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
      // left over from an earlier choice: no list and no consent mode are chosen
      recipientListId: null,
      unsubscribed: 'resubscribe',
      bouncedAddress: 'skipAndReport',
      consent: 'none',
      consentId: 21,
      revokedConsent: 'reAgree',
    };

    expect(toSaveRequest(options, { ...CONTEXT, invalidEmailApplies: false })).toEqual({
      ...NO_LIST_OR_CONSENT,
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

  it('sends the chosen list and consent options', () => {
    const options: OptionsDto = {
      ...DEFAULT_OPTIONS,
      recipientListId: 12,
      unsubscribed: 'resubscribe',
      bouncedAddress: 'skipAndReport',
      consent: 'blanketWithColumnOverride',
      consentId: 22,
      revokedConsent: 'reAgree',
    };

    expect(toSaveRequest(options, CONTEXT)).toEqual(options);
  });

  it('never sends a list or consent that does not exist, or the column mode without a column', () => {
    const options: OptionsDto = {
      ...DEFAULT_OPTIONS,
      recipientListId: 99,
      unsubscribed: 'resubscribe',
      consent: 'blanketWithColumnOverride',
      consentId: 21,
      revokedConsent: 'reAgree',
    };

    expect(toSaveRequest(options, CONTEXT)).toMatchObject({ recipientListId: null, unsubscribed: 'keep', consent: 'blanketWithColumnOverride', consentId: 21 });
    expect(toSaveRequest(options, { ...CONTEXT, consentColumnMapped: false })).toMatchObject({ consent: 'none', consentId: null, revokedConsent: 'keep' });
    expect(toSaveRequest({ ...options, consent: 'blanket', consentId: 99 }, CONTEXT)).toMatchObject({ consent: 'blanket', consentId: null });
  });
});

describe('isSameOptions', () => {
  it('compares what the server would store', () => {
    const a = { ...DEFAULT_OPTIONS, existingContacts: 'skip' as const, emptyValues: 'clear' as const };
    const b = { ...DEFAULT_OPTIONS, existingContacts: 'skip' as const };
    expect(isSameOptions(a, b, CONTEXT)).toBe(true);
    expect(isSameOptions(DEFAULT_OPTIONS, { ...DEFAULT_OPTIONS, emptyValues: 'clear' }, CONTEXT)).toBe(false);
    expect(isSameOptions(DEFAULT_OPTIONS, { ...DEFAULT_OPTIONS, reportIncludesKey: true }, CONTEXT)).toBe(false);
    expect(isSameOptions(DEFAULT_OPTIONS, { ...DEFAULT_OPTIONS, recipientListId: 11 }, CONTEXT)).toBe(false);
    expect(isSameOptions({ ...DEFAULT_OPTIONS, recipientListId: 11 }, { ...DEFAULT_OPTIONS, recipientListId: 11, unsubscribed: 'resubscribe' }, CONTEXT)).toBe(false);
    expect(isSameOptions(DEFAULT_OPTIONS, { ...DEFAULT_OPTIONS, unsubscribed: 'resubscribe' }, CONTEXT)).toBe(true);
    expect(isSameOptions({ ...DEFAULT_OPTIONS, consent: 'blanket', consentId: 21 }, { ...DEFAULT_OPTIONS, consent: 'blanket', consentId: 22 }, CONTEXT)).toBe(false);
  });
});
