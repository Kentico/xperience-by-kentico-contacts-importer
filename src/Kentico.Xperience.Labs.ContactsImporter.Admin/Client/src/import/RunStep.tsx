import { Dialog, Headline, HeadlineSize, Spacing, Stack } from '@kentico/xperience-admin-components';
import React, { useEffect } from 'react';
import { useBlocker } from 'react-router';

import type { ExecuteCommand } from '../shared/commands';
import { Strings } from '../shared/strings';
import { Text } from '../shared/ui';

import { CancelImport } from './CancelImport';
import { progressFor } from './model';
import type { JobView } from './model';
import { ProcessPanel } from './ProcessPanel';
import { useProcessLoop } from './useProcessLoop';

const S = Strings.run;

export interface RunStepProps {
  readonly execute: ExecuteCommand;
  readonly view: JobView;
  /** The job changed on the server (import finished, failed, cancelled, changed elsewhere): load it again. */
  readonly onReload: () => void;
}

/**
 * Run step: drives the import with `PROCESS_NEXT` calls, shows progress and running counts,
 * and why the import waits when it does. Leaving the page pauses the import.
 */
export const RunStep = ({ execute, view, onReload }: RunStepProps) => {
  const loop = useProcessLoop({
    jobId: view.job.jobId,
    phase: 'execute',
    execute,
    initialProgress: progressFor('execute', view.progress),
    autoStart: true,
  });
  const { status } = loop.snapshot;
  const active = status === 'running' || status === 'pausing';

  useEffect(() => {
    if (status === 'finished') {
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
      event.returnValue = true;
    };
    window.addEventListener('beforeunload', onBeforeUnload);

    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [active]);

  // Ask before navigating inside the administration while the import runs.
  const blocker = useBlocker(active);

  return (
    <Stack spacing={Spacing.L}>
      <Headline size={HeadlineSize.M}>{S.headline}</Headline>
      <ProcessPanel
        view={loop}
        runningText={loop.snapshot.calls === 0 ? S.starting : S.running}
        cards={S.cards}
        titleName={view.job.file.name}
        actions={
          <CancelImport execute={execute} message={S.cancelMessage} keepLabel={S.cancelKeep} onBeforeCancel={loop.pause} onCancelled={onReload} />
        }
      />

      <Dialog
        isOpen={blocker.state === 'blocked'}
        headline={Strings.leave.headline}
        headerCloseButton={{ tooltipText: Strings.leave.close }}
        isDismissable={true}
        onClose={() => blocker.reset?.()}
        confirmAction={{ label: Strings.leave.leave, onClick: () => blocker.proceed?.() }}
        cancelAction={{ label: Strings.leave.stay, onClick: () => blocker.reset?.() }}
      >
        <Text>{S.leaveMessage}</Text>
      </Dialog>
    </Stack>
  );
};
