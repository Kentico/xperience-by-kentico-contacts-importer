import {
  Button,
  ButtonColor,
  Callout,
  CalloutPlacementType,
  CalloutType,
  Spacing,
} from "@kentico/xperience-admin-components";
import React, { useCallback, useEffect, useRef, useState } from "react";

import type { ExecuteCommand, RunningImportDto } from "../shared/commands";
import { useText } from "../shared/i18n";
import { TestIds, testId } from "../shared/ui";

import {
  runningImportHeadline,
  runningImportLines,
  watchRunningImport,
} from "./running-import";
import type { OtherRuns, RunningImportWatch } from "./running-import";

const NO_RUNS: OtherRuns = { blocker: null, lastRunEndedUtc: null };

/**
 * Watches the other imports' Runs while `enabled` (PLAN D60). `refresh` checks again at once (for
 * example after the server refused the confirmation or the start).
 */
export function useOtherRuns(
  execute: ExecuteCommand,
  enabled: boolean,
): OtherRuns & { readonly refresh: () => void } {
  const [runs, setRuns] = useState<OtherRuns>(NO_RUNS);
  const watchRef = useRef<RunningImportWatch | null>(null);
  useEffect(() => {
    if (!enabled) {
      return undefined;
    }

    const watch = watchRunningImport(execute, setRuns);
    watchRef.current = watch;

    return () => {
      watch.stop();
      watchRef.current = null;
      setRuns(NO_RUNS);
    };
  }, [enabled, execute]);

  const refresh = useCallback(() => watchRef.current?.refresh(), []);

  return { ...(enabled ? runs : NO_RUNS), refresh };
}

export interface OtherImportCalloutProps {
  readonly running: RunningImportDto;
  /** Opens the other import (its page), where it can be continued or cancelled. */
  readonly onOpenImport: (jobId: string) => void;
}

/** Names the import whose unfinished Run keeps this one from running, with a button to open it. */
export const OtherImportCallout = ({
  running,
  onOpenImport,
}: OtherImportCalloutProps) => {
  const { t, m, dateTime } = useText();

  return (
    <div
      data-testid={TestIds.otherImportRunning}
      data-other-job-id={running.jobId}
      data-other-state={running.state}
    >
      <Callout
        type={CalloutType.FriendlyWarning}
        placement={CalloutPlacementType.OnDesk}
        headline={m(runningImportHeadline(running))}
        actionButton={
          <Button
            label={t("contactsimporter.review.otherrunning.open")}
            color={ButtonColor.Secondary}
            dataTestId={testId(TestIds.openOtherImport)}
            onClick={() => {
              onOpenImport(running.jobId);
            }}
          />
        }
      >
        <div
          style={{ display: "flex", flexDirection: "column", gap: Spacing.XS }}
        >
          {runningImportLines(running, dateTime).map((line) => (
            <div key={line.key}>{m(line)}</div>
          ))}
          <div style={{ marginTop: Spacing.S }}>
            {t("contactsimporter.review.otherrunning.text")}
          </div>
        </div>
      </Callout>
    </div>
  );
};
