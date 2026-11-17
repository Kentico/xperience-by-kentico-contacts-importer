import type { JobDto } from '../shared/commands';
import { Strings, format } from '../shared/strings';

const S = Strings.consentSummary;

/** The part of the job the recipient list and consent summary is built from. */
export type ConsentSummaryJob = Pick<JobDto, 'options' | 'recipientListName' | 'consentName' | 'consentMapping'>;

/** Sentences that say what the saved options do to the recipient list and to consent. */
export function consentSummaryLines(job: ConsentSummaryJob): string[] {
  const { options } = job;
  const lines: string[] = [];

  if (options.recipientListId === null) {
    lines.push(S.noList);
  } else {
    lines.push(format(S.list, job.recipientListName ?? format(S.missing, options.recipientListId)));
    lines.push(S.listAdd);
    lines.push(options.unsubscribed === 'resubscribe' ? S.unsubscribedResubscribe : S.unsubscribedKeep);
    lines.push(options.bouncedAddress === 'skipAndReport' ? S.bouncedSkip : S.bouncedAdd);
  }

  if (options.consent === 'none') {
    lines.push(S.noConsent);
  } else {
    lines.push(format(S.consent, job.consentName ?? format(S.missing, options.consentId ?? 0)));
    if (options.consent === 'blanketWithColumnOverride') {
      lines.push(format(S.consentColumn, job.consentMapping?.agreed ?? ''));
      if (job.consentMapping?.date) {
        lines.push(format(S.consentDate, job.consentMapping.date));
      }
    } else {
      lines.push(S.consentBlanket);
    }
    lines.push(options.revokedConsent === 'reAgree' ? S.revokedReAgree : S.revokedKeep);
  }

  return lines;
}
