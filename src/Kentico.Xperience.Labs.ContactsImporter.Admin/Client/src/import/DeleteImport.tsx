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

export interface DeleteImportProps {
  readonly execute: ExecuteCommand;
  readonly jobId: string;
  readonly fileName: string;
  /** The import is deleted (or was already gone). */
  readonly onDeleted: () => void;
}

/** "Delete" on Results: the same as the Delete action of the imports list (D51), with the same texts. */
export const DeleteImport = ({
  execute,
  jobId,
  fileName,
  onDeleted,
}: DeleteImportProps) => {
  const { t } = useText();
  const isMounted = useIsMounted();
  const [open, setOpen] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const confirm = useCallback(async () => {
    setDeleting(true);
    setError(null);
    const response = await execute<ImportActionResponse, DeleteImportRequest>(
      Commands.DeleteImport,
      {
        driverToken: currentDriverToken(jobId),
      },
    );
    if (!isMounted()) {
      return;
    }

    setDeleting(false);
    if (response?.code === "ok") {
      setOpen(false);
      onDeleted();
    } else {
      setError(response?.message ?? t("contactsimporter.list.deletefailed"));
    }
  }, [execute, isMounted, jobId, onDeleted, t]);

  return (
    <>
      <Button
        label={t("contactsimporter.list.delete")}
        color={ButtonColor.Secondary}
        destructive={true}
        dataTestId={testId(TestIds.resultsDelete)}
        onClick={() => {
          setError(null);
          setOpen(true);
        }}
      />
      <Dialog
        maxWidth={DIALOG_MAX_WIDTH}
        isOpen={open}
        headline={t("contactsimporter.list.deleteheadline")}
        headerCloseButton={{ tooltipText: t("contactsimporter.leave.close") }}
        isDismissable={!deleting}
        actionInProgress={deleting}
        onClose={() => {
          setOpen(false);
        }}
        confirmAction={{
          label: t("contactsimporter.list.deleteconfirm"),
          destructive: true,
          onClick: () => void confirm(),
        }}
        cancelAction={{
          label: t("contactsimporter.list.cancel"),
          onClick: () => {
            setOpen(false);
          },
        }}
      >
        {error && <NotificationBarAlert>{error}</NotificationBarAlert>}
        <Text>{t("contactsimporter.list.deletemessage", { fileName })}</Text>
      </Dialog>
    </>
  );
};
