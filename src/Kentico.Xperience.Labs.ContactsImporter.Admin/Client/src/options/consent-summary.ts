import type { JobDto } from '../shared/commands';
import type { Message } from '../shared/text';

/** The part of the job the recipient list and consent summary is built from. */
export type ConsentSummaryJob = Pick<JobDto, 'options' | 'recipientListName' | 'consentName' | 'consentMapping'>;

/** Sentences that say what the saved options do to the recipient list and to consent. */
export function consentSummaryLines(job: ConsentSummaryJob): Message[] {
  const { options } = job;
  const lines: Message[] = [];

  if (options.recipientListId === null) {
    lines.push({ key: 'contactsimporter.consentsummary.nolist' });
  } else {
    lines.push(
      job.recipientListName !== null && job.recipientListName !== undefined
        ? { key: 'contactsimporter.consentsummary.list', params: { list: job.recipientListName } }
        : { key: 'contactsimporter.consentsummary.listmissing', params: { id: String(options.recipientListId) } },
    );
    lines.push({ key: 'contactsimporter.consentsummary.listadd' });
    lines.push({
      key: options.unsubscribed === 'resubscribe' ? 'contactsimporter.consentsummary.unsubscribedresubscribe' : 'contactsimporter.consentsummary.unsubscribedkeep',
    });
    lines.push({
      key: options.bouncedAddress === 'skipAndReport' ? 'contactsimporter.consentsummary.bouncedskip' : 'contactsimporter.consentsummary.bouncedadd',
    });
  }

  if (options.consent === 'none') {
    lines.push({ key: 'contactsimporter.consentsummary.noconsent' });
  } else {
    lines.push(
      job.consentName !== null && job.consentName !== undefined
        ? { key: 'contactsimporter.consentsummary.consent', params: { consent: job.consentName } }
        : { key: 'contactsimporter.consentsummary.consentmissing', params: { id: String(options.consentId ?? 0) } },
    );
    if (options.consent === 'blanketWithColumnOverride') {
      lines.push({ key: 'contactsimporter.consentsummary.consentcolumn', params: { column: job.consentMapping?.agreed ?? '' } });
      if (job.consentMapping?.date) {
        lines.push({ key: 'contactsimporter.consentsummary.consentdate', params: { column: job.consentMapping.date } });
      }
    } else {
      lines.push({ key: 'contactsimporter.consentsummary.consentblanket' });
    }
    lines.push({
      key: options.revokedConsent === 'reAgree' ? 'contactsimporter.consentsummary.revokedreagree' : 'contactsimporter.consentsummary.revokedkeep',
    });
  }

  return lines;
}
