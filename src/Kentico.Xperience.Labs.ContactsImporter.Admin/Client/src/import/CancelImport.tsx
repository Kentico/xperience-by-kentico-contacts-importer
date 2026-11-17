import { Button, ButtonColor, Dialog, NotificationBarAlert } from '@kentico/xperience-admin-components';
import React, { useCallback, useState } from 'react';

import { Commands } from '../shared/commands';
import type { ExecuteCommand, ImportActionResponse } from '../shared/commands';
import { useIsMounted } from '../shared/hooks';
import { useText } from '../shared/i18n';
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
  const { t } = useText();
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
      setError(response?.message ?? t('contactsimporter.run.cancelfailed'));
    }
  }, [execute, isMounted, onBeforeCancel, onCancelled, t]);

  return (
    <>
      <Button
        label={t('contactsimporter.run.cancel')}
        color={ButtonColor.Secondary}
        destructive={true}
        dataTestId={testId(TestIds.runCancel)}
        onClick={() => setOpen(true)}
      />
      <Dialog
        isOpen={open}
        headline={t('contactsimporter.run.cancelheadline')}
        headerCloseButton={{ tooltipText: t('contactsimporter.leave.close') }}
        isDismissable={!cancelling}
        actionInProgress={cancelling}
        onClose={() => setOpen(false)}
        confirmAction={{ label: t('contactsimporter.run.cancelconfirm'), destructive: true, onClick: () => void cancel() }}
        cancelAction={{ label: keepLabel, onClick: () => setOpen(false) }}
      >
        {error && <NotificationBarAlert>{error}</NotificationBarAlert>}
        <Text>{message}</Text>
      </Dialog>
    </>
  );
};
