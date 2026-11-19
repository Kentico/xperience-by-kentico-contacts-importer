import {
  Button,
  ButtonColor,
  ButtonSize,
  Callout,
  CalloutPlacementType,
  CalloutType,
  Cols,
  Column,
  Dialog,
  Icon,
  InfoCard,
  NotificationBarAlert,
  NotificationBarInfo,
  NotificationBarWarning,
  Paper,
  Row,
  Spacing,
  Stack,
} from "@kentico/xperience-admin-components";
import React, { useEffect, useState } from "react";
import { useBlocker } from "react-router";

import type {
  ImportProfile,
  PipelineResult,
  RejectReason,
} from "../import-core";
import type { JobDto, StatusDto } from "../shared/commands";
import { useText } from "../shared/i18n";
import {
  downloadTextFile,
  rejectReasonKey,
  rejectedRowsCsv,
  reportFileName,
  suspectRowsCsv,
} from "../shared/reports";
import {
  DIALOG_MAX_WIDTH,
  ProgressTrack,
  TestIds,
  Text,
  testId,
} from "../shared/ui";
import { estimateTotalRows, percent } from "../upload/progress";
import type { RateEstimator } from "../upload/progress";
import { uploadBarView } from "../upload/upload-bar";
import type { UploadPhase } from "../upload/upload-bar";
import type { UploadProgress } from "../upload/upload-session";

import type { UploadController } from "./useUploadSession";

/** A running upload shows its bar only when it is still running after this time (small files: no flash). */
const REVEAL_DELAY_MS = 400;

export interface UploadBarProps {
  readonly upload: UploadController;
  readonly job: JobDto;
  readonly status: StatusDto;
  /** Show the one-line summary of a finished upload (Options and Review). */
  readonly showSummary: boolean;
  /**
   * The bar is the side column of the step (Options: right of the cards, sticky so it stays in view
   * while the user scrolls). Without it, the bar is a row above the step.
   */
  readonly side?: boolean;
}

/** Width of the side column; it wraps below the step content on narrow screens. */
const SIDE_WIDTH = "360px";

/**
 * Upload in the background (D50): a small bar with progress and a toggle for the details (counts,
 * reports, pause / continue). Problems open the details. A finished upload leaves a one-line
 * summary whose details keep the client-side reports downloadable.
 */
export const UploadBar = ({
  upload,
  job,
  status,
  showSummary,
  side = false,
}: UploadBarProps) => {
  const { t, number, bytes, percent: formatPercent } = useText();
  const bar = uploadBarView({
    phase: upload.phase,
    jobState: status.state,
    retrying: (upload.progress?.retry ?? null) !== null,
  });
  const [open, setOpen] = useState(false);
  const revealed = useRevealAfter(bar.delayed, REVEAL_DELAY_MS);

  // Problems need the user: open the details when one appears.
  const [hadProblem, setHadProblem] = useState(false);
  if (bar.problem !== hadProblem) {
    setHadProblem(bar.problem);
    if (bar.problem) {
      setOpen(true);
    }
  }

  const pipeline = upload.progress?.pipeline;
  const completed = pipeline
    ? percent(pipeline.bytesRead, pipeline.totalBytes)
    : 0;
  const profile =
    upload.result?.profile ??
    pipeline?.profile ??
    (bar.phase === "done" ? status.profile : undefined);

  const toggle = (
    <Button
      label={
        open
          ? t("contactsimporter.upload.bar.hidedetails")
          : t("contactsimporter.upload.bar.showdetails")
      }
      color={ButtonColor.Tertiary}
      size={ButtonSize.S}
      trailingIcon={open ? "xp-chevron-up" : "xp-chevron-down"}
      dataTestId={testId(TestIds.uploadDetailsToggle)}
      onClick={() => {
        setOpen((current) => !current);
      }}
    />
  );

  let content: React.ReactNode = null;
  if (bar.mode === "progress" && (!bar.delayed || revealed)) {
    const rate = upload.estimator.estimate(
      pipeline?.totalBytes ?? 0,
    ).ratePerSecond;
    const lines =
      bar.phase === "running"
        ? upload.progress?.phase === "completing"
          ? [t("contactsimporter.upload.completing")]
          : [
              t("contactsimporter.upload.bar.running", {
                percent: formatPercent(completed),
              }),
              ...(rate > 0
                ? [t("contactsimporter.upload.bar.rate", { rate: bytes(rate) })]
                : []),
            ]
        : bar.phase === "failed"
          ? [
              t("contactsimporter.upload.bar.failed", {
                percent: formatPercent(completed),
              }),
            ]
          : [
              t("contactsimporter.upload.bar.paused", {
                percent: formatPercent(completed),
              }),
            ];

    content = (
      <Stack spacing={Spacing.S}>
        <BarLines lines={lines} />
        <ProgressTrack completed={completed} />
        <div>{toggle}</div>
        {open && (
          <Details
            upload={upload}
            job={job}
            phase={bar.phase}
            profile={profile}
            compact={side}
          />
        )}
      </Stack>
    );
  } else if (bar.mode === "summary" && showSummary) {
    content = (
      <Stack spacing={Spacing.S}>
        <BarLines
          lines={[
            t("contactsimporter.upload.bar.done"),
            t("contactsimporter.upload.bar.rows", {
              rows: number(status.profile.rowsValid),
            }),
            t("contactsimporter.upload.bar.skipped", {
              skipped: number(status.profile.rowsRejected),
            }),
          ]}
          success={true}
        />
        <div>{toggle}</div>
        {open && (
          <Details
            upload={upload}
            job={job}
            phase="done"
            profile={profile}
            compact={side}
          />
        )}
      </Stack>
    );
  }

  return (
    <>
      {/* Exact upload state and counts for tests; the bar itself shows them formatted or not at all. */}
      <span
        hidden={true}
        data-testid={TestIds.uploadStatus}
        data-upload-phase={bar.phase}
        data-job-state={status.state}
        data-rows-uploaded={profile?.rowsValid ?? 0}
      >
        {profile && (
          <>
            <Count name="rows-read" value={profile.rowsRead} />
            <Count name="rows-uploaded" value={profile.rowsValid} />
            <Count name="rows-skipped" value={profile.rowsRejected} />
            <Count name="likely-duplicates" value={profile.likelyDuplicates} />
          </>
        )}
      </span>
      {content &&
        (side ? (
          <div
            style={{
              flex: `0 1 ${SIDE_WIDTH}`,
              minWidth: 0,
              position: "sticky",
              top: Spacing.XL,
            }}
          >
            <BarPaper mode={bar.mode}>{content}</BarPaper>
          </div>
        ) : (
          <BarPaper mode={bar.mode}>{content}</BarPaper>
        ))}
      {upload.phase === "running" && <UploadLeaveGuard />}
    </>
  );
};

const BarPaper = ({
  mode,
  children,
}: {
  readonly mode: string;
  readonly children: React.ReactNode;
}) => (
  <div data-testid={TestIds.uploadBar} data-bar-mode={mode}>
    <Paper>
      <div style={{ padding: `${Spacing.M} ${Spacing.XL}` }}>{children}</div>
    </Paper>
  </div>
);

interface BarLinesProps {
  /** One data point per line; the first is the state. */
  readonly lines: readonly string[];
  /** Finished upload: check mark and state line in the success color. */
  readonly success?: boolean;
}

const BarLines = ({ lines, success = false }: BarLinesProps) => {
  const [first, ...rest] = lines;

  return (
    <div
      data-testid={TestIds.uploadBarText}
      style={{ display: "flex", flexDirection: "column", gap: Spacing.XS }}
    >
      <span
        style={{ display: "inline-flex", alignItems: "center", gap: Spacing.S }}
      >
        {success && (
          <span
            style={{
              display: "inline-flex",
              color: "var(--color-success-icon)",
            }}
          >
            <Icon name="xp-check-circle" />
          </span>
        )}
        <Text
          strong={true}
          style={success ? { color: "var(--color-success-text)" } : undefined}
        >
          {first}
        </Text>
      </span>
      {rest.map((line) => (
        <Text key={line}>{line}</Text>
      ))}
    </div>
  );
};

/** True once `active` has stayed true for `delayMs`; false again when it turns false. */
function useRevealAfter(active: boolean, delayMs: number): boolean {
  const [revealed, setRevealed] = useState(false);

  useEffect(() => {
    if (!active) {
      return undefined;
    }

    const timer = window.setTimeout(() => {
      setRevealed(true);
    }, delayMs);

    return () => {
      window.clearTimeout(timer);
      setRevealed(false);
    };
  }, [active, delayMs]);

  return active && revealed;
}

/** Asks before navigating inside the administration mid-upload. Mounted only while uploading: a router keeps one blocker. */
const UploadLeaveGuard = () => {
  const { t } = useText();
  const blocker = useBlocker(true);

  return (
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
      <Text>{t("contactsimporter.leave.message")}</Text>
    </Dialog>
  );
};

interface DetailsProps {
  readonly upload: UploadController;
  readonly job: JobDto;
  readonly phase: UploadPhase;
  readonly profile: ImportProfile | undefined;
  /** Narrow side column: count cards in two columns. */
  readonly compact: boolean;
}

/** Full upload details (the former Upload step): status, notices, counts, reports, pause / continue. */
const Details = ({ upload, job, phase, profile, compact }: DetailsProps) => {
  const { t, bytes, percent: formatPercent } = useText();
  const { progress, result, error } = upload;
  const running = phase === "running";
  const pipeline = progress?.pipeline;
  const completed = pipeline
    ? percent(pipeline.bytesRead, pipeline.totalBytes)
    : 0;
  const fromEarlierVisit = phase === "done" && !result;

  return (
    <div data-testid={TestIds.uploadDetails} style={{ paddingTop: Spacing.M }}>
      <div style={{ display: "flex", flexDirection: "column", gap: Spacing.L }}>
        {running && (
          <Text>
            {progress?.phase === "completing"
              ? t("contactsimporter.upload.completing")
              : t("contactsimporter.upload.working")}
          </Text>
        )}
        {(phase === "paused" || phase === "idle") && (
          <Text>{t("contactsimporter.upload.paused")}</Text>
        )}
        {fromEarlierVisit && (
          <Text hint={true}>{t("contactsimporter.upload.bar.earlier")}</Text>
        )}

        {running && (
          <Callout
            type={CalloutType.QuickTip}
            placement={CalloutPlacementType.OnDesk}
          >
            {t("contactsimporter.upload.keepopen")}
          </Callout>
        )}

        {pipeline && phase !== "done" && (
          <Text>
            {t("contactsimporter.upload.progress", {
              read: bytes(pipeline.bytesRead),
              total: bytes(pipeline.totalBytes),
              percent: formatPercent(completed),
            })}
          </Text>
        )}

        {progress?.retry && running && (
          <NotificationBarWarning>
            {t("contactsimporter.upload.retrying", {
              part: progress.retry.index + 1,
              seconds: Math.ceil(progress.retry.delayMs / 1000),
              attempt: progress.retry.attempt + 1,
              maxAttempts: progress.retry.maxAttempts,
            })}
          </NotificationBarWarning>
        )}
        {progress?.truncatedFrom !== null &&
          progress?.truncatedFrom !== undefined && (
            <NotificationBarInfo>
              {t("contactsimporter.upload.truncated")}
            </NotificationBarInfo>
          )}
        {progress && progress.stats.skippedChunks > 0 && (
          <NotificationBarInfo>
            {t("contactsimporter.upload.resumed", {
              count: progress.stats.skippedChunks,
              amount: progress.stats.skippedChunks,
            })}
          </NotificationBarInfo>
        )}
        {error && <NotificationBarAlert>{error}</NotificationBarAlert>}

        {profile && (
          <Cards
            profile={profile}
            progress={progress}
            estimator={upload.estimator}
            done={phase === "done"}
            elapsedSeconds={upload.elapsedSeconds}
            compact={compact}
          />
        )}

        {profile && profile.likelyDuplicates > 0 && (
          <Callout
            type={CalloutType.FriendlyWarning}
            placement={CalloutPlacementType.OnDesk}
            headline={t("contactsimporter.upload.duplicatesheadline", {
              amount: profile.likelyDuplicates,
            })}
            actionButton={
              result && result.suspectRows.length > 0 ? (
                <Button
                  label={t("contactsimporter.upload.downloadduplicates")}
                  color={ButtonColor.Secondary}
                  dataTestId={testId(TestIds.downloadDuplicates)}
                  onClick={() => {
                    downloadTextFile(
                      reportFileName(job.file.name, "likely-duplicates"),
                      suspectRowsCsv(result.suspectRows),
                    );
                  }}
                />
              ) : undefined
            }
          >
            {t("contactsimporter.upload.duplicatestext")}
            {result?.suspectRowsTruncated
              ? ` ${t("contactsimporter.upload.listtruncated", { amount: result.suspectRows.length })}`
              : ""}
          </Callout>
        )}

        {result && result.profile.rowsRejected > 0 && (
          <Callout
            type={CalloutType.FriendlyWarning}
            placement={CalloutPlacementType.OnDesk}
            headline={t("contactsimporter.upload.rejectedheadline", {
              amount: result.profile.rowsRejected,
            })}
            actionButton={
              <Button
                label={t("contactsimporter.upload.downloadrejected")}
                color={ButtonColor.Secondary}
                dataTestId={testId(TestIds.downloadRejected)}
                onClick={() => {
                  downloadTextFile(
                    reportFileName(job.file.name, "skipped-rows"),
                    rejectedRowsCsv(result.rejectedRows, (reason) =>
                      t(rejectReasonKey(reason)),
                    ),
                  );
                }}
              />
            }
          >
            <RejectReasons result={result} />
            {t("contactsimporter.upload.rejectedtext")}
            {result.rejectedRowsTruncated
              ? ` ${t("contactsimporter.upload.listtruncated", { amount: result.rejectedRows.length })}`
              : ""}
          </Callout>
        )}

        {phase !== "done" && (
          <div style={{ display: "flex", gap: Spacing.M }}>
            {running ? (
              <Button
                label={t("contactsimporter.upload.pause")}
                color={ButtonColor.Secondary}
                dataTestId={testId(TestIds.uploadPause)}
                onClick={upload.pause}
              />
            ) : (
              upload.hasFile && (
                <Button
                  label={
                    phase === "failed"
                      ? t("contactsimporter.upload.retry")
                      : phase === "paused"
                        ? t("contactsimporter.upload.resume")
                        : t("contactsimporter.upload.start")
                  }
                  color={ButtonColor.Primary}
                  dataTestId={testId(TestIds.uploadResume)}
                  onClick={upload.resume}
                />
              )
            )}
          </div>
        )}
      </div>
    </div>
  );
};

interface CardsProps {
  readonly profile: ImportProfile;
  readonly progress: UploadProgress | null;
  readonly estimator: RateEstimator;
  readonly done: boolean;
  readonly elapsedSeconds: number | null;
  readonly compact: boolean;
}

const Cards = ({
  profile,
  progress,
  estimator,
  done,
  elapsedSeconds,
  compact,
}: CardsProps) => {
  const { t, number, bytes, duration } = useText();
  const pipeline = progress?.pipeline;
  const totalRows = pipeline
    ? estimateTotalRows(
        profile.rowsRead,
        pipeline.bytesRead,
        pipeline.totalBytes,
      )
    : profile.rowsRead;
  const wholeFileRead =
    done || !pipeline || pipeline.bytesRead >= pipeline.totalBytes;
  const estimate = pipeline
    ? estimator.estimate(pipeline.totalBytes)
    : { ratePerSecond: 0, etaSeconds: null };
  const stats = progress?.stats;
  const stored = stats
    ? stats.uploadedChunks + stats.skippedChunks
    : profile.chunkCount;
  const created = progress
    ? Math.max(progress.chunksCreated, stored)
    : profile.chunkCount;

  let speedDetail: string = t(
    "contactsimporter.upload.cards.speeddetailunknown",
  );
  if (done) {
    speedDetail =
      elapsedSeconds !== null
        ? t("contactsimporter.upload.cards.speeddetaildone", {
            duration: duration(elapsedSeconds),
          })
        : "";
  } else if (estimate.etaSeconds !== null) {
    speedDetail = t("contactsimporter.upload.cards.speeddetail", {
      duration: duration(estimate.etaSeconds),
    });
  }

  const cards: {
    readonly key: string;
    readonly cols: Cols;
    readonly card: React.ReactNode;
  }[] = [
    {
      key: "rows",
      cols: Cols.Col3,
      card: (
        <InfoCard
          caption={t("contactsimporter.upload.cards.rows")}
          tooltip={t("contactsimporter.upload.cards.rowstooltip")}
          text={number(profile.rowsRead)}
          details={
            wholeFileRead
              ? t("contactsimporter.upload.cards.rowsdetailexact")
              : t("contactsimporter.upload.cards.rowsdetail", {
                  total: totalRows,
                })
          }
        />
      ),
    },
    {
      key: "chunks",
      cols: Cols.Col3,
      card: (
        <InfoCard
          caption={t("contactsimporter.upload.cards.chunks")}
          tooltip={t("contactsimporter.upload.cards.chunkstooltip")}
          text={number(profile.rowsValid)}
          details={
            stats
              ? t("contactsimporter.upload.cards.chunksdetail", {
                  stored,
                  created,
                  sent: bytes(stats.uploadedBytes),
                })
              : t("contactsimporter.upload.cards.chunksdetailstored", {
                  count: stored,
                  stored,
                })
          }
        />
      ),
    },
    ...(progress
      ? [
          {
            key: "speed",
            cols: Cols.Col2,
            card: (
              <InfoCard
                caption={t("contactsimporter.upload.cards.speed")}
                tooltip={t("contactsimporter.upload.cards.speedtooltip")}
                text={
                  done || estimate.ratePerSecond <= 0
                    ? "–"
                    : t("contactsimporter.upload.cards.speedvalue", {
                        rate: bytes(estimate.ratePerSecond),
                      })
                }
                details={speedDetail}
              />
            ),
          },
        ]
      : []),
    {
      key: "rejected",
      cols: Cols.Col2,
      card: (
        <InfoCard
          caption={t("contactsimporter.upload.cards.rejected")}
          tooltip={t("contactsimporter.upload.cards.rejectedtooltip")}
          text={number(profile.rowsRejected)}
          details={t("contactsimporter.upload.cards.rejecteddetail", {
            amount: profile.rowsValid,
          })}
        />
      ),
    },
    {
      key: "duplicates",
      cols: Cols.Col2,
      card: (
        <InfoCard
          caption={t("contactsimporter.upload.cards.duplicates")}
          tooltip={t("contactsimporter.upload.cards.duplicatestooltip")}
          text={number(profile.likelyDuplicates)}
          details={t("contactsimporter.upload.cards.duplicatesdetail")}
        />
      ),
    },
  ];

  // Side column: one card per line.
  if (compact) {
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: Spacing.M }}>
        {cards.map(({ key, card }) => (
          <div key={key}>{card}</div>
        ))}
      </div>
    );
  }

  return (
    <Row spacing={Spacing.L}>
      {cards.map(({ key, cols, card }) => (
        <Column key={key} cols={cols}>
          {card}
        </Column>
      ))}
    </Row>
  );
};

/** Exact value of a count for tests; the cards show it formatted. */
const Count = ({
  name,
  value,
}: {
  readonly name: string;
  readonly value: number;
}) => (
  <span
    hidden={true}
    data-testid={`${TestIds.countPrefix}${name}`}
    data-count={value}
  />
);

const RejectReasons = ({ result }: { readonly result: PipelineResult }) => {
  const { t, number } = useText();
  const entries = Object.entries(result.rejectedByReason) as [
    RejectReason,
    number,
  ][];
  if (entries.length === 0) {
    return null;
  }

  return (
    <ul style={{ margin: `0 0 ${Spacing.S} 0`, paddingLeft: Spacing.XL }}>
      {entries.map(([reason, count]) => (
        <li key={reason}>
          {t(rejectReasonKey(reason))}: {number(count)}
        </li>
      ))}
    </ul>
  );
};
