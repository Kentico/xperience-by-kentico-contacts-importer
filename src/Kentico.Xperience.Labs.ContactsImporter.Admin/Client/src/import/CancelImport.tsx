import { Button, ButtonColor, Dialog, NotificationBarAlert } from '@kentico/xperience-admin-components';
import React, { useCallback, useState } from 'react';

import { Commands } from '../shared/commands';
import type { ExecuteCommand, ImportActionResponse } from '../shared/commands';
import { useIsMounted } from '../shared/hooks';
import { Strings } from '../shared/strings';
import { TestIds, Text, testId } from '../shared/ui';

export interface CancelImportProps {
  readonly execute: ExecuteCommand;
  /** Dialog text: what cancelling means at this step. */
  readonly message: string;
  readonly keepLabel: string;
  /** Runs before the command is sent (stop sending processing calls). */
  readonly onBeforeCancel?: () => void;
  /** The job is cancelled (or was already finished): reload it. */
  readonly onCancelled: () => void;
}

/** "Cancel import" button with its confirmation; sends `CANCEL_IMPORT`. */
export const CancelImport = ({ execute, message, keepLabel, onBeforeCancel, onCancelled }: CancelImportProps) => {
  const isMounted = useIsMounted();
  const [open, setOpen] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const cancel = useCallback(async () => {
    setCancelling(true);
    setError(null);
    onBeforeCancel?.();
    const response = await execute<ImportActionResponse>(Commands.CancelImport);
    if (!isMounted()) {
      return;
    }

    setCancelling(false);
    if (response?.code === 'ok' || response?.code === 'invalidState') {
      // invalidState: the import finished meanwhile; the reload shows how.
      setOpen(false);
      onCancelled();
    } else {
      setError(response?.message ?? Strings.run.cancelFailed);
    }
  }, [execute, isMounted, onBeforeCancel, onCancelled]);

  return (
    <>
      <Button
        label={Strings.run.cancel}
        color={ButtonColor.Secondary}
        destructive={true}
        dataTestId={testId(TestIds.runCancel)}
        onClick={() => setOpen(true)}
      />
      <Dialog
        isOpen={open}
        headline={Strings.run.cancelHeadline}
        headerCloseButton={{ tooltipText: Strings.leave.close }}
        isDismissable={!cancelling}
        actionInProgress={cancelling}
        onClose={() => setOpen(false)}
        confirmAction={{ label: Strings.run.cancelConfirm, destructive: true, onClick: () => void cancel() }}
        cancelAction={{ label: keepLabel, onClick: () => setOpen(false) }}
      >
        {error && <NotificationBarAlert>{error}</NotificationBarAlert>}
        <Text>{message}</Text>
      </Dialog>
    </>
  );
};
