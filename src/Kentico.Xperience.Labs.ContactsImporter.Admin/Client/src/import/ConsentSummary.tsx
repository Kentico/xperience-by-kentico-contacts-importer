import {
  Headline,
  HeadlineSize,
  Spacing,
  Stack,
} from "@kentico/xperience-admin-components";
import React from "react";

import { consentSummaryLines } from "../options/consent-summary";
import type { JobDto } from "../shared/commands";
import { useText } from "../shared/i18n";
import { TestIds, Text } from "../shared/ui";

export interface ConsentSummaryProps {
  readonly job: JobDto;
}

/**
 * What the import does to the recipient list and to consent, in sentences, and who confirmed it.
 * Shown on Review and Results so every choice that affects consent stays visible.
 */
export const ConsentSummary = ({ job }: ConsentSummaryProps) => {
  const { t, m, dateTime } = useText();
  const nothing =
    job.options.recipientListId === null && job.options.consent === "none";

  return (
    <Stack spacing={Spacing.S}>
      <Headline size={HeadlineSize.S}>
        {t("contactsimporter.consentsummary.headline")}
      </Headline>
      <div
        data-testid={TestIds.consentSummary}
        data-recipient-list-id={job.options.recipientListId ?? ""}
        data-unsubscribed={job.options.unsubscribed}
        data-bounced-address={job.options.bouncedAddress}
        data-consent={job.options.consent}
        data-consent-id={job.options.consentId ?? ""}
        data-revoked-consent={job.options.revokedConsent}
      >
        {nothing ? (
          <Text>{t("contactsimporter.consentsummary.none")}</Text>
        ) : (
          consentSummaryLines(job).map((line) => (
            <Text key={line.key}>{m(line)}</Text>
          ))
        )}
      </div>
      {job.attestation && (
        <Text
          hint={true}
          data-testid={TestIds.attestationRecord}
          data-statement-version={job.attestation.statementVersion}
        >
          {t("contactsimporter.consentsummary.attestation", {
            user:
              job.attestation.userName === ""
                ? t("contactsimporter.consentsummary.attestationunknownuser")
                : job.attestation.userName,
            time: dateTime(job.attestation.utc),
            version: job.attestation.statementVersion,
          })}
        </Text>
      )}
    </Stack>
  );
};
