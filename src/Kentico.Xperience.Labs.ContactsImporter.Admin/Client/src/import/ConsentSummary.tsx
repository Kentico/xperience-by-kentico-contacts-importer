import { Headline, HeadlineSize, Spacing, Stack } from '@kentico/xperience-admin-components';
import React from 'react';

import { consentSummaryLines } from '../options/consent-summary';
import type { JobDto } from '../shared/commands';
import { formatDateTime } from '../shared/format';
import { Strings, format } from '../shared/strings';
import { TestIds, Text } from '../shared/ui';

const S = Strings.consentSummary;

export interface ConsentSummaryProps {
  readonly job: JobDto;
}

/**
 * What the import does to the recipient list and to consent, in sentences, and who confirmed it.
 * Shown on Review and Results so every choice that affects consent stays visible.
 */
export const ConsentSummary = ({ job }: ConsentSummaryProps) => {
  const nothing = job.options.recipientListId === null && job.options.consent === 'none';

  return (
    <Stack spacing={Spacing.S}>
      <Headline size={HeadlineSize.S}>{S.headline}</Headline>
      <div
        data-testid={TestIds.consentSummary}
        data-recipient-list-id={job.options.recipientListId ?? ''}
        data-unsubscribed={job.options.unsubscribed}
        data-bounced-address={job.options.bouncedAddress}
        data-consent={job.options.consent}
        data-consent-id={job.options.consentId ?? ''}
        data-revoked-consent={job.options.revokedConsent}
      >
        {nothing ? <Text>{S.none}</Text> : consentSummaryLines(job).map((line) => <Text key={line}>{line}</Text>)}
      </div>
      {job.attestation && (
        <Text hint={true} data-testid={TestIds.attestationRecord} data-statement-version={job.attestation.statementVersion}>
          {format(
            S.attestation,
            job.attestation.userName === '' ? S.attestationUnknownUser : job.attestation.userName,
            formatDateTime(job.attestation.utc),
            job.attestation.statementVersion,
          )}
        </Text>
      )}
    </Stack>
  );
};
