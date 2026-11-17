import {
  Button,
  ButtonColor,
  Callout,
  CalloutPlacementType,
  CalloutType,
  Cols,
  Column,
  InfoCard,
  Inline,
  NotificationBarAlert,
  NotificationBarInfo,
  NotificationBarWarning,
  ProgressBar,
  Row,
  Spacing,
  Stack,
} from '@kentico/xperience-admin-components';
import React, { useEffect } from 'react';

import type { AggregatesDto } from '../shared/commands';
import { formatDateTime, formatDuration, formatNumber, formatPercent } from '../shared/format';
import { Strings, format } from '../shared/strings';
import { TestIds, Text, testId } from '../shared/ui';
import { percent } from '../upload/progress';

import { Count } from './Aggregates';
import type { ProcessLoopView } from './useProcessLoop';

const S = Strings.process;

/** The four running counts of a phase; texts differ between the check and the import. */
export interface CountCardTexts {
  readonly created: string;
  readonly createdDetail: string;
  readonly updated: string;
  readonly updatedDetail: string;
  readonly unchanged: string;
  readonly unchangedDetail: string;
  readonly skipped: string;
  readonly skippedDetail: string;
  readonly createdTooltip: string;
  readonly updatedTooltip: string;
  readonly unchangedTooltip: string;
  readonly skippedTooltip: string;
}

export interface ProcessPanelProps {
  readonly view: ProcessLoopView;
  /** What is happening, shown while the loop runs. */
  readonly runningText: string;
  readonly cards: CountCardTexts;
  /** Name for the browser tab title next to the percentage. */
  readonly titleName: string;
  /** The job adds contacts to a recipient list: the running count of subscribed contacts is shown. */
  readonly usesList?: boolean;
  /** The job records consent agreements: their running count is shown. */
  readonly usesConsent?: boolean;
  /** Extra buttons next to Pause / Continue (cancel, back). */
  readonly actions?: React.ReactNode;
}

/**
 * Progress of the analysis or the import (PLAN §10 "Progress UI"): rows done of total, speed and
 * time left, running counts from the server, and why the work waits when it does.
 */
export const ProcessPanel = ({ view, runningText, cards, titleName, usesList = false, usesConsent = false, actions }: ProcessPanelProps) => {
  const { snapshot, rate, lockedByOtherTab } = view;
  const progress = snapshot.progress;
  const done = progress?.rowsProcessed ?? 0;
  const total = progress?.rowsTotal ?? 0;
  const completed = percent(done, total);
  const active = snapshot.status === 'running' || snapshot.status === 'pausing';
  const pauseReason = lockedByOtherTab ? 'activeElsewhere' : snapshot.status === 'paused' ? snapshot.pauseReason : null;
  const waitsForUser = pauseReason !== null || snapshot.status === 'idle';

  // Progress in the tab title, so it is visible from other tabs.
  useEffect(() => {
    if (!active) {
      return undefined;
    }

    const original = document.title;
    document.title = format(Strings.run.title, formatPercent(completed), titleName);

    return () => {
      document.title = original;
    };
  }, [active, completed, titleName]);

  return (
    <Stack spacing={Spacing.L}>
      <Text
        data-testid={TestIds.processStatus}
        data-process-status={lockedByOtherTab ? 'paused' : snapshot.status}
        data-process-phase={snapshot.phase}
        data-pause-reason={pauseReason ?? ''}
        data-rows-processed={done}
        data-calls={snapshot.calls}
        data-answers={snapshot.processedCalls}
      >
        {snapshot.status === 'pausing' ? S.pausing : active ? runningText : ''}
      </Text>

      {active && (
        <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk}>
          {S.keepOpen}
        </Callout>
      )}

      <Stack spacing={Spacing.S}>
        <ProgressBar completed={completed} />
        <Text>{format(S.progress, formatNumber(done), formatNumber(total), formatPercent(completed))}</Text>
      </Stack>

      {snapshot.retry && active && (
        <NotificationBarWarning>
          {format(
            snapshot.retry.kind === 'network' ? S.retryNetwork : S.retryServer,
            Math.ceil(snapshot.retry.delayMs / 1000),
            snapshot.retry.attempt,
            snapshot.retry.maxAttempts,
          )}
        </NotificationBarWarning>
      )}

      {pauseReason !== null &&
        (pauseReason === 'user' ? (
          <NotificationBarInfo>{S.paused.user}</NotificationBarInfo>
        ) : pauseReason === 'network' || pauseReason === 'server' ? (
          <NotificationBarAlert>
            {S.paused[pauseReason]}
            {snapshot.error ? ` ${snapshot.error}` : ''}
          </NotificationBarAlert>
        ) : (
          <NotificationBarWarning>{S.paused[pauseReason]}</NotificationBarWarning>
        ))}
      {waitsForUser && snapshot.lastAnswerAtMs !== null && (
        <Text hint={true}>{format(S.lastProgress, formatDateTime(new Date(snapshot.lastAnswerAtMs).toISOString()))}</Text>
      )}

      {progress && <CountCards aggregates={progress.aggregates} cards={cards} ratePerSecond={active ? rate.ratePerSecond : 0} etaSeconds={active ? rate.etaSeconds : null} />}
      {progress && (usesList || usesConsent) && (
        <Row spacing={Spacing.L}>
          {usesList && (
            <Column cols={Cols.Col3}>
              <Count name="running-list" value={(progress.aggregates.listAdded ?? 0) + (progress.aggregates.listResubscribed ?? 0)} />
              <InfoCard
                caption={Strings.run.listCard}
                tooltip={Strings.run.listCardTooltip}
                text={formatNumber((progress.aggregates.listAdded ?? 0) + (progress.aggregates.listResubscribed ?? 0))}
                details={Strings.run.listCardDetail}
              />
            </Column>
          )}
          {usesConsent && (
            <Column cols={Cols.Col3}>
              <Count name="running-consent" value={(progress.aggregates.consentRecorded ?? 0) + (progress.aggregates.consentReAgreed ?? 0)} />
              <InfoCard
                caption={Strings.run.consentCard}
                tooltip={Strings.run.consentCardTooltip}
                text={formatNumber((progress.aggregates.consentRecorded ?? 0) + (progress.aggregates.consentReAgreed ?? 0))}
                details={Strings.run.consentCardDetail}
              />
            </Column>
          )}
        </Row>
      )}

      <Inline spacing={Spacing.M}>
        {active && (
          <Button
            label={S.pause}
            color={ButtonColor.Secondary}
            disabled={snapshot.status === 'pausing'}
            dataTestId={testId(TestIds.processPause)}
            onClick={view.pause}
          />
        )}
        {waitsForUser && (
          <Button
            label={pauseReason === 'stale' || pauseReason === 'activeElsewhere' || pauseReason === 'takenOver' ? S.takeOver : S.continue}
            color={ButtonColor.Primary}
            dataTestId={testId(TestIds.processContinue)}
            onClick={() => view.start(pauseReason === 'stale' || pauseReason === 'activeElsewhere' || pauseReason === 'takenOver')}
          />
        )}
        {actions}
      </Inline>
    </Stack>
  );
};

interface CountCardsProps {
  readonly aggregates: AggregatesDto;
  readonly cards: CountCardTexts;
  readonly ratePerSecond: number;
  readonly etaSeconds: number | null;
}

const CountCards = ({ aggregates, cards, ratePerSecond, etaSeconds }: CountCardsProps) => (
  <Row spacing={Spacing.L}>
    <Column cols={Cols.Col2}>
      <Count name="running-created" value={aggregates.created} />
      <InfoCard caption={cards.created} tooltip={cards.createdTooltip} text={formatNumber(aggregates.created)} details={cards.createdDetail} />
    </Column>
    <Column cols={Cols.Col2}>
      <Count name="running-updated" value={aggregates.contactsUpdated} />
      <InfoCard caption={cards.updated} tooltip={cards.updatedTooltip} text={formatNumber(aggregates.contactsUpdated)} details={cards.updatedDetail} />
    </Column>
    <Column cols={Cols.Col2}>
      <Count name="running-unchanged" value={aggregates.unchanged} />
      <InfoCard caption={cards.unchanged} tooltip={cards.unchangedTooltip} text={formatNumber(aggregates.unchanged)} details={cards.unchangedDetail} />
    </Column>
    <Column cols={Cols.Col2}>
      <Count name="running-skipped" value={aggregates.skipped} />
      <InfoCard caption={cards.skipped} tooltip={cards.skippedTooltip} text={formatNumber(aggregates.skipped)} details={cards.skippedDetail} />
    </Column>
    <Column cols={Cols.Col3}>
      <InfoCard
        caption={S.speed}
        tooltip={S.speedTooltip}
        text={ratePerSecond > 0 ? format(S.speedValue, formatNumber(ratePerSecond)) : '–'}
        details={etaSeconds !== null ? format(S.speedDetail, formatDuration(etaSeconds)) : ratePerSecond > 0 ? S.speedDetailUnknown : ''}
      />
    </Column>
  </Row>
);
