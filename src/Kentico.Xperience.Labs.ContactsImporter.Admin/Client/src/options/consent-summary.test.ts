import { describe, expect, it } from 'vitest';

import { consentSummaryLines } from './consent-summary';
import type { ConsentSummaryJob } from './consent-summary';
import { DEFAULT_OPTIONS } from './options-state';

const NOTHING: ConsentSummaryJob = { options: DEFAULT_OPTIONS, recipientListName: null, consentName: null, consentMapping: null };

describe('consentSummaryLines', () => {
  it('says that nothing changes with the default options', () => {
    expect(consentSummaryLines(NOTHING)).toEqual(['Recipient lists are not changed.', 'Consent agreements are not recorded.']);
  });

  it('names the list and every list choice', () => {
    const lines = consentSummaryLines({ ...NOTHING, options: { ...DEFAULT_OPTIONS, recipientListId: 11 }, recipientListName: 'Newsletter' });

    expect(lines).toEqual([
      'Recipient list: Newsletter',
      'Contacts that are not on the list are added as subscribed, without a confirmation email.',
      'Contacts who unsubscribed from the list stay unsubscribed.',
      'Contacts whose email address bounced are added to the list. Xperience does not send emails to them.',
      'Consent agreements are not recorded.',
    ]);
  });

  it('says when unsubscribed contacts are subscribed again and bounced addresses are left out', () => {
    const lines = consentSummaryLines({
      ...NOTHING,
      options: { ...DEFAULT_OPTIONS, recipientListId: 11, unsubscribed: 'resubscribe', bouncedAddress: 'skipAndReport' },
      recipientListName: 'Newsletter',
    });

    expect(lines).toContain('Contacts who unsubscribed from the list are subscribed again.');
    expect(lines).toContain('Contacts whose email address bounced are not added to the list.');
    expect(lines).not.toContain('Contacts who unsubscribed from the list stay unsubscribed.');
  });

  it('names the consent and how it is recorded', () => {
    expect(
      consentSummaryLines({ ...NOTHING, options: { ...DEFAULT_OPTIONS, consent: 'blanket', consentId: 21 }, consentName: 'Webinar signup' }),
    ).toEqual([
      'Recipient lists are not changed.',
      'Consent: Webinar signup',
      'An agreement is recorded for every contact in the file.',
      'Contacts who withdrew the consent stay withdrawn.',
    ]);

    expect(
      consentSummaryLines({
        options: { ...DEFAULT_OPTIONS, consent: 'blanketWithColumnOverride', consentId: 21, revokedConsent: 'reAgree' },
        recipientListName: null,
        consentName: 'Webinar signup',
        consentMapping: { agreed: 'Opt in', date: 'Opt in date' },
      }),
    ).toEqual([
      'Recipient lists are not changed.',
      'Consent: Webinar signup',
      'An agreement is recorded only for rows where the column "Opt in" says yes.',
      'Agreement dates come from the column "Opt in date".',
      'A new agreement is recorded for contacts who withdrew the consent.',
    ]);
  });

  it('shows a deleted list or consent by its ID instead of hiding the choice', () => {
    const lines = consentSummaryLines({
      ...NOTHING,
      options: { ...DEFAULT_OPTIONS, recipientListId: 11, consent: 'blanket', consentId: 21 },
    });

    expect(lines).toContain('Recipient list: no longer exists (ID 11)');
    expect(lines).toContain('Consent: no longer exists (ID 21)');
  });
});
