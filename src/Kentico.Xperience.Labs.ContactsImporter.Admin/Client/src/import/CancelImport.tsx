import {
  Button,
  ButtonColor,
  Dialog,
  NotificationBarAlert,
} from "@kentico/xperience-admin-components";
import React, { useCallback, useState } from "react";

import { Commands } from "../shared/commands";
import type {
  DeleteImportRequest,
  ExecuteCommand,
  ImportActionResponse,
} from "../shared/commands";
import { useIsMounted } from "../shared/hooks";
import { useText } from "../shared/i18n";
import { DIALOG_MAX_WIDTH, TestIds, Text, testId } from "../shared/ui";

import { currentDriverToken } from "./useProcessLoop";

export interface CancelImportProps {
  readonly execute: ExecuteCommand;
  /**
   * Before Run (D51), pass the job ID: cancelling deletes the whole import (`DELETE_IMPORT`). Without it
   * (Run step) the import is cancelled (`CANCEL_IMPORT`) and stays, with its partial results.
   */
  readonly deleteJobId?: string;
  /** Dialog text: what cancelling means at this step. */
  readonly message: string;
  readonly keepLabel: string;
  /** Runs before the command is sent (stop sending processing calls, stop the upload). */
  readonly onBeforeCancel?: () => void;
  /** The job is cancelled (or was already finished), or deleted. */
  readonly onCancelled: () => void;
}

/** Sends the cancel command; `ok` = done. Returns the message to show otherwise. */
async function sendCancel(
  execute: ExecuteCommand,
  deleteJobId: string | undefined,
): Promise<{ done: boolean; message: string | null }> {
  if (deleteJobId !== undefined) {
    const response = await execute<ImportActionResponse, DeleteImportRequest>(
      Commands.DeleteImport,
      {
        driverToken: currentDriverToken(deleteJobId),
      },
    );

    return {
      done: response?.code === "ok",
      message: response?.message ?? null,
    };
  }

  const response = await execute<ImportActionResponse>(Commands.CancelImport);

  // invalidState: the import finished meanwhile; the reload shows how.
  return {
    done: response?.code === "ok" || response?.code === "invalidState",
    message: response?.message ?? null,
  };
}

/** "Cancel import" button with its confirmation; sends `DELETE_IMPORT` (before Run) or `CANCEL_IMPORT` (Run). */
export const CancelImport = ({
  execute,
  deleteJobId,
  message,
  keepLabel,
  onBeforeCancel,
  onCancelled,
}: CancelImportProps) => {
  const { t } = useText();
  const isMounted = useIsMounted();
  const [open, setOpen] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const cancel = useCallback(async () => {
    setCancelling(true);
    setError(null);
    onBeforeCancel?.();
    const result = await sendCancel(execute, deleteJobId);
    if (!isMounted()) {
      return;
    }

    setCancelling(false);
    if (result.done) {
      setOpen(false);
      onCancelled();
    } else {
      setError(result.message ?? t("contactsimporter.run.cancelfailed"));
    }
  }, [deleteJobId, execute, isMounted, onBeforeCancel, onCancelled, t]);

  return (
    <>
      <Button
        label={t("contactsimporter.run.cancel")}
        color={ButtonColor.Secondary}
        destructive={true}
        dataTestId={testId(TestIds.runCancel)}
        onClick={() => {
          setError(null);
          setOpen(true);
        }}
      />
      <Dialog
        maxWidth={DIALOG_MAX_WIDTH}
        isOpen={open}
        headline={t("contactsimporter.run.cancelheadline")}
        headerCloseButton={{ tooltipText: t("contactsimporter.leave.close") }}
        isDismissable={!cancelling}
        actionInProgress={cancelling}
        onClose={() => {
          setOpen(false);
        }}
        confirmAction={{
          label: t("contactsimporter.run.cancelconfirm"),
          destructive: true,
          onClick: () => void cancel(),
        }}
        cancelAction={{
          label: keepLabel,
          onClick: () => {
            setOpen(false);
          },
        }}
      >
        {error && <NotificationBarAlert>{error}</NotificationBarAlert>}
        <Text>{message}</Text>
      </Dialog>
    </>
  );
};
