import { Button, ButtonColor, NotificationBarAlert, Spacing, Stack } from '@kentico/xperience-admin-components';
import React, { useCallback, useEffect, useRef, useState } from 'react';

import { downloadReport, saveBlob } from '../process/report-download';
import { Commands } from '../shared/commands';
import type { ExecuteCommand, GetReportRequest, GetReportResponse, ProcessPhase } from '../shared/commands';
import { formatNumber } from '../shared/format';
import { useIsMounted } from '../shared/hooks';
import { Strings, format } from '../shared/strings';
import { TestIds, Text, testId } from '../shared/ui';

export interface ReportButtonProps {
  readonly execute: ExecuteCommand;
  readonly phase: ProcessPhase;
  readonly label: string;
  readonly hint?: string;
}

/** Downloads a row report page by page (`GET_REPORT`) and saves it as one `.csv.gz` file. */
export const ReportButton = ({ execute, phase, label, hint }: ReportButtonProps) => {
  const isMounted = useIsMounted();
  const abortRef = useRef<AbortController | null>(null);
  const [rows, setRows] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => () => abortRef.current?.abort(), []);

  const download = useCallback(async () => {
    const controller = new AbortController();
    abortRef.current = controller;
    setError(null);
    setRows(0);
    try {
      const file = await downloadReport({
        phase,
        signal: controller.signal,
        call: (request, signal) => execute<GetReportResponse, GetReportRequest>(Commands.GetReport, request, signal),
        onProgress: (received) => isMounted() && setRows(received),
      });
      if (isMounted()) {
        saveBlob(file.fileName, file.blob);
      }
    } catch (reason) {
      if (isMounted() && !controller.signal.aborted) {
        setError(format(Strings.review.downloadFailed, reason instanceof Error ? reason.message : ''));
      }
    } finally {
      if (isMounted()) {
        setRows(null);
      }
    }
  }, [execute, isMounted, phase]);

  return (
    <Stack spacing={Spacing.S}>
      {error && <NotificationBarAlert onDismiss={() => setError(null)}>{error}</NotificationBarAlert>}
      <div>
        <Button
          label={rows === null ? label : format(Strings.review.downloading, formatNumber(rows))}
          color={ButtonColor.Secondary}
          icon="xp-arrow-down-line"
          disabled={rows !== null}
          inProgress={rows !== null}
          dataTestId={testId(`${TestIds.downloadReportPrefix}${phase}`)}
          onClick={() => void download()}
        />
      </div>
      {hint && <Text hint={true}>{hint}</Text>}
    </Stack>
  );
};
