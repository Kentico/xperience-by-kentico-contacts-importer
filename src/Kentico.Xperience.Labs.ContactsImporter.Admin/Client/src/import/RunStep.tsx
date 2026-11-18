import {
  Dialog,
  Headline,
  HeadlineSize,
  Spacing,
  Stack,
} from "@kentico/xperience-admin-components";
import React, { useEffect, useMemo } from "react";
import { useBlocker } from "react-router";

import type { ExecuteCommand } from "../shared/commands";
import { useText } from "../shared/i18n";
import { DelayedReveal, DIALOG_MAX_WIDTH, Text } from "../shared/ui";

import { CancelImport } from "./CancelImport";
import { progressFor } from "./model";
import type { JobView } from "./model";
import { OtherImportCallout, useOtherRuns } from "./OtherImportRunning";
import { ProcessPanel } from "./ProcessPanel";
import type { CountCardTexts } from "./ProcessPanel";
import { useProcessLoop } from "./useProcessLoop";

export interface RunStepProps {
  readonly execute: ExecuteCommand;
  readonly view: JobView;
  /** The job changed on the server (import finished, failed, cancelled, changed elsewhere): load it again. */
  readonly onReload: () => void;
  /** Opens another import (the one whose unfinished Run keeps this one from starting). */
  readonly onOpenImport: (jobId: string) => void;
}

/**
 * Run step: drives the import with `PROCESS_NEXT` calls, shows progress and running counts,
 * and why the import waits when it does. Leaving the page pauses the import.
 */
export const RunStep = ({
  execute,
  view,
  onReload,
  onOpenImport,
}: RunStepProps) => {
  const { t } = useText();
  const cards = useMemo<CountCardTexts>(
    () => ({
      created: t("contactsimporter.run.cards.created"),
      createdDetail: t("contactsimporter.run.cards.createddetail"),
      createdTooltip: t("contactsimporter.run.cards.createdtooltip"),
      updated: t("contactsimporter.run.cards.updated"),
      updatedDetail: t("contactsimporter.run.cards.updateddetail"),
      updatedTooltip: t("contactsimporter.run.cards.updatedtooltip"),
      unchanged: t("contactsimporter.run.cards.unchanged"),
      unchangedDetail: t("contactsimporter.run.cards.unchangeddetail"),
      unchangedTooltip: t("contactsimporter.run.cards.unchangedtooltip"),
      skipped: t("contactsimporter.run.cards.skipped"),
      skippedDetail: t("contactsimporter.run.cards.skippeddetail"),
      skippedTooltip: t("contactsimporter.run.cards.skippedtooltip"),
    }),
    [t],
  );
  const loop = useProcessLoop({
    jobId: view.job.jobId,
    phase: "execute",
    execute,
    initialProgress: progressFor("execute", view.progress),
    // Continues by itself unless a user paused it with Pause (then it waits for Continue).
    autoStart: true,
    pausedByUser: view.status.pausedByUser,
  });
  const { status } = loop.snapshot;
  // Only while it runs or is pausing: a paused import (also one opened paused) can be left without a question.
  const active = status === "running" || status === "pausing";
  // The server refused to start the Run (PLAN D60): show the other import and watch it until it ends.
  const blockedByOther =
    status === "paused" && loop.snapshot.pauseReason === "otherImportRunning";
  const otherRuns = useOtherRuns(execute, blockedByOther);
  const otherImport = blockedByOther ? otherRuns.blocker : null;

  useEffect(() => {
    if (status === "finished") {
      onReload();
    }
  }, [status, onReload]);

  // Warn before the tab closes or reloads while the import runs.
  useEffect(() => {
    if (!active) {
      return undefined;
    }

    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      // Legacy way to show the prompt, for browsers that ignore preventDefault() here
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      event.returnValue = true;
    };
    window.addEventListener("beforeunload", onBeforeUnload);

    return () => {
      window.removeEventListener("beforeunload", onBeforeUnload);
    };
  }, [active]);

  // Ask before navigating inside the administration while the import runs.
  const blocker = useBlocker(active);

  return (
    <DelayedReveal>
      <Stack spacing={Spacing.L}>
        <Headline size={HeadlineSize.M}>
          {t("contactsimporter.run.headline")}
        </Headline>
        <ProcessPanel
          view={loop}
          runningText={
            loop.snapshot.calls === 0
              ? t("contactsimporter.run.starting")
              : t("contactsimporter.run.running")
          }
          cards={cards}
          titleName={view.job.file.name}
          usesList={view.job.options.recipientListId !== null}
          usesConsent={view.job.options.consent !== "none"}
          pauseDetails={
            otherImport && (
              <OtherImportCallout
                running={otherImport}
                onOpenImport={onOpenImport}
              />
            )
          }
          continueDisabled={otherImport !== null}
          actions={
            <CancelImport
              execute={execute}
              message={t("contactsimporter.run.cancelmessage")}
              keepLabel={t("contactsimporter.run.cancelkeep")}
              onBeforeCancel={loop.pause}
              onCancelled={onReload}
            />
          }
        />

        <Dialog
          maxWidth={DIALOG_MAX_WIDTH}
          isOpen={blocker.state === "blocked"}
          headline={t("contactsimporter.leave.headline")}
          headerCloseButton={{ tooltipText: t("contactsimporter.leave.close") }}
          isDismissable={true}
          onClose={() => blocker.reset?.()}
          confirmAction={{
            label: t("contactsimporter.leave.leave"),
            onClick: () => blocker.proceed?.(),
          }}
          cancelAction={{
            label: t("contactsimporter.leave.stay"),
            onClick: () => blocker.reset?.(),
          }}
        >
          <Text>{t("contactsimporter.run.leavemessage")}</Text>
        </Dialog>
      </Stack>
    </DelayedReveal>
  );
};
