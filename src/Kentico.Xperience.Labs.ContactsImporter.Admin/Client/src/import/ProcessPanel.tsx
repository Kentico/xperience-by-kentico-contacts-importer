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
  Row,
  Spacing,
  Stack,
} from '@kentico/xperience-admin-components';
import React, { useEffect } from 'react';

import type { AggregatesDto } from '../shared/commands';
import { useText } from '../shared/i18n';
import { ProgressTrack, TestIds, Text, testId } from '../shared/ui';
import { percent } from '../upload/progress';

import { Count } from './Aggregates';
import type { ProcessLoopView } from './useProcessLoop';

/** Text of each reason the work waits for the user. */
const PAUSE_KEYS = {
  user: 'contactsimporter.process.paused.user',
  network: 'contactsimporter.process.paused.network',
  server: 'contactsimporter.process.paused.server',
  activeElsewhere: 'contactsimporter.process.paused.activeelsewhere',
  stale: 'contactsimporter.process.paused.stale',
  takenOver: 'contactsimporter.process.paused.takenover',
  otherImportRunning: 'contactsimporter.process.paused.otherimportrunning',
} as const;

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
  /** Shown below the reason the work waits (the other import that blocks the Run). */
  readonly pauseDetails?: React.ReactNode;
  /** Continue cannot work yet (another import's Run is unfinished). */
  readonly continueDisabled?: boolean;
}

/**
 * Progress of the analysis or the import (PLAN §10 "Progress UI"): rows done of total, speed and
 * time left, running counts from the server, and why the work waits when it does.
 */
export const ProcessPanel = ({
  view,
  runningText,
  cards,
  titleName,
  usesList = false,
  usesConsent = false,
  actions,
  pauseDetails,
  continueDisabled = false,
}: ProcessPanelProps) => {
  const { t, number, percent: formatPercent, dateTime } = useText();
  const { snapshot, rate, lockedByOtherTab, waitingForOther } = view;
  const progress = snapshot.progress;
  const done = progress?.rowsProcessed ?? 0;
  const total = progress?.rowsTotal ?? 0;
  const completed = percent(done, total);
  // Waiting after "Continue here" counts as active: Pause stops waiting, no second "Continue here".
  const active = snapshot.status === 'running' || snapshot.status === 'pausing' || waitingForOther;
  const pauseReason = lockedByOtherTab ? 'activeElsewhere' : snapshot.status === 'paused' ? snapshot.pauseReason : null;
  const waitsForUser = !waitingForOther && (pauseReason !== null || snapshot.status === 'idle');

  // Progress in the tab title, so it is visible from other tabs.
  useEffect(() => {
    if (!active) {
      return undefined;
    }

    const original = document.title;
    document.title = t('contactsimporter.run.title', { percent: formatPercent(completed), fileName: titleName });

    return () => {
      document.title = original;
    };
  }, [active, completed, formatPercent, t, titleName]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: Spacing.L }}>
      <Text
        data-testid={TestIds.processStatus}
        data-process-status={lockedByOtherTab ? 'paused' : snapshot.status}
        data-process-phase={snapshot.phase}
        data-pause-reason={pauseReason ?? ''}
        data-rows-processed={done}
        data-calls={snapshot.calls}
        data-answers={snapshot.processedCalls}
      >
        {snapshot.status === 'running' || waitingForOther ? runningText : ''}
      </Text>

      {/* The call in flight finishes first (the server stops at its next batch): say so, it is not stuck. */}
      {snapshot.status === 'pausing' && (
        <div data-testid={TestIds.processPausing}>
          <NotificationBarInfo>{t('contactsimporter.process.pausing')}</NotificationBarInfo>
        </div>
      )}

      {waitingForOther && (
        <div data-testid={TestIds.processWaitingForOther}>
          <NotificationBarInfo>{t('contactsimporter.process.waitingforother')}</NotificationBarInfo>
        </div>
      )}

      {active && (
        <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk}>
          {t('contactsimporter.process.keepopen')}
        </Callout>
      )}

      <Stack spacing={Spacing.S}>
        <ProgressTrack completed={completed} />
        <Text>{t('contactsimporter.process.progress', { done, total, percent: formatPercent(completed) })}</Text>
      </Stack>

      {snapshot.retry && active && (
        <NotificationBarWarning>
          {t(snapshot.retry.kind === 'network' ? 'contactsimporter.process.retrynetwork' : 'contactsimporter.process.retryserver', {
            seconds: Math.ceil(snapshot.retry.delayMs / 1000),
            attempt: snapshot.retry.attempt,
            maxAttempts: snapshot.retry.maxAttempts,
          })}
        </NotificationBarWarning>
      )}

      {pauseReason !== null &&
        (pauseReason === 'user' ? (
          <NotificationBarInfo>{t(PAUSE_KEYS.user)}</NotificationBarInfo>
        ) : pauseReason === 'network' || pauseReason === 'server' ? (
          <NotificationBarAlert>
            {t(PAUSE_KEYS[pauseReason])}
            {snapshot.error ? ` ${snapshot.error}` : ''}
          </NotificationBarAlert>
        ) : (
          <NotificationBarWarning>{t(PAUSE_KEYS[pauseReason])}</NotificationBarWarning>
        ))}
      {pauseReason !== null && pauseDetails}
      {waitsForUser && snapshot.lastAnswerAtMs !== null && (
        <Text hint={true}>{t('contactsimporter.process.lastprogress', { time: dateTime(new Date(snapshot.lastAnswerAtMs).toISOString()) })}</Text>
      )}

      {progress && <CountCards aggregates={progress.aggregates} cards={cards} ratePerSecond={active ? rate.ratePerSecond : 0} etaSeconds={active ? rate.etaSeconds : null} />}
      {progress && (usesList || usesConsent) && (
        <Row spacing={Spacing.L}>
          {usesList && (
            <Column cols={Cols.Col3}>
              <Count name="running-list" value={(progress.aggregates.listAdded ?? 0) + (progress.aggregates.listResubscribed ?? 0)} />
              <InfoCard
                caption={t('contactsimporter.run.listcard')}
                tooltip={t('contactsimporter.run.listcardtooltip')}
                text={number((progress.aggregates.listAdded ?? 0) + (progress.aggregates.listResubscribed ?? 0))}
                details={t('contactsimporter.run.listcarddetail')}
              />
            </Column>
          )}
          {usesConsent && (
            <Column cols={Cols.Col3}>
              <Count name="running-consent" value={(progress.aggregates.consentRecorded ?? 0) + (progress.aggregates.consentReAgreed ?? 0)} />
              <InfoCard
                caption={t('contactsimporter.run.consentcard')}
                tooltip={t('contactsimporter.run.consentcardtooltip')}
                text={number((progress.aggregates.consentRecorded ?? 0) + (progress.aggregates.consentReAgreed ?? 0))}
                details={t('contactsimporter.run.consentcarddetail')}
              />
            </Column>
          )}
        </Row>
      )}

      <Inline spacing={Spacing.M}>
        {active && (
          <Button
            label={snapshot.status === 'pausing' ? t('contactsimporter.process.pausingbutton') : t('contactsimporter.process.pause')}
            color={ButtonColor.Secondary}
            disabled={snapshot.status === 'pausing'}
            inProgress={snapshot.status === 'pausing'}
            dataTestId={testId(TestIds.processPause)}
            onClick={view.pause}
          />
        )}
        {waitsForUser && (
          <Button
            label={pauseReason === 'stale' || pauseReason === 'activeElsewhere' || pauseReason === 'takenOver' ? t('contactsimporter.process.takeover') : t('contactsimporter.process.continue')}
            color={ButtonColor.Primary}
            disabled={continueDisabled}
            dataTestId={testId(TestIds.processContinue)}
            onClick={() => view.start(pauseReason === 'stale' || pauseReason === 'activeElsewhere' || pauseReason === 'takenOver')}
          />
        )}
        {actions}
      </Inline>
    </div>
  );
};

interface CountCardsProps {
  readonly aggregates: AggregatesDto;
  readonly cards: CountCardTexts;
  readonly ratePerSecond: number;
  readonly etaSeconds: number | null;
}

const CountCards = ({ aggregates, cards, ratePerSecond, etaSeconds }: CountCardsProps) => {
  const { t, number, duration } = useText();

  return (
  <Row spacing={Spacing.L}>
    <Column cols={Cols.Col2}>
      <Count name="running-created" value={aggregates.created} />
      <InfoCard caption={cards.created} tooltip={cards.createdTooltip} text={number(aggregates.created)} details={cards.createdDetail} />
    </Column>
    <Column cols={Cols.Col2}>
      <Count name="running-updated" value={aggregates.contactsUpdated} />
      <InfoCard caption={cards.updated} tooltip={cards.updatedTooltip} text={number(aggregates.contactsUpdated)} details={cards.updatedDetail} />
    </Column>
    <Column cols={Cols.Col2}>
      <Count name="running-unchanged" value={aggregates.unchanged} />
      <InfoCard caption={cards.unchanged} tooltip={cards.unchangedTooltip} text={number(aggregates.unchanged)} details={cards.unchangedDetail} />
    </Column>
    <Column cols={Cols.Col2}>
      <Count name="running-skipped" value={aggregates.skipped} />
      <InfoCard caption={cards.skipped} tooltip={cards.skippedTooltip} text={number(aggregates.skipped)} details={cards.skippedDetail} />
    </Column>
    <Column cols={Cols.Col3}>
      <InfoCard
        caption={t('contactsimporter.process.speed')}
        tooltip={t('contactsimporter.process.speedtooltip')}
        text={ratePerSecond > 0 ? t('contactsimporter.process.speedvalue', { rate: ratePerSecond }) : '–'}
        details={
          etaSeconds !== null
            ? t('contactsimporter.process.speeddetail', { duration: duration(etaSeconds) })
            : ratePerSecond > 0
              ? t('contactsimporter.process.speeddetailunknown')
              : ''
        }
      />
    </Column>
  </Row>
  );
};
