import {
  Box,
  Button,
  ButtonColor,
  Callout,
  CalloutPlacementType,
  CalloutType,
  NotificationBarAlert,
  Spacing,
  Spinner,
  Stack,
} from '@kentico/xperience-admin-components';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { createMappingState, isSameMapping, toConsentMapping, toFieldMapping, validateMapping } from '../mapping/mapping-state';
import type { MappingState, SavedMapping } from '../mapping/mapping-state';
import { Commands } from '../shared/commands';
import type {
  ImportConfigResponse,
  ImportJobResponse,
  JobDto,
  ProgressDto,
  ReportsDto,
  SaveConfigRequest,
  SaveConfigResponse,
  StatusDto,
} from '../shared/commands';
import { useAdminNavigate, useExecuteCommand, useIsMounted } from '../shared/hooks';
import { useText } from '../shared/i18n';
import { TestIds, Text, testId } from '../shared/ui';
import { ImportWorkerClient } from '../worker/client';
import { createImportWorker } from '../worker/create-worker';

import { FileStep, isSameFile } from './FileStep';
import type { SelectedFile } from './FileStep';
import { MapStep } from './MapStep';
import { DEFAULT_SOURCE_SETTINGS, stepForState, toJobView, toTargetFields } from './model';
import type { JobView, Step } from './model';
import { OptionsStep } from './OptionsStep';
import { ResultsStep } from './ResultsStep';
import { ReviewStep } from './ReviewStep';
import { RunStep } from './RunStep';
import { StepHeader } from './StepHeader';
import { UploadStep } from './UploadStep';

/** Client properties of `ImportPage` (UIPages/ImportPage.cs). */
interface ImportProps {
  readonly canImport: boolean;
  readonly jobId: string;
  readonly importsListPath: string;
  readonly contactGroupsPath: string;
  /** The user may run imports that change recipient list subscriptions or record consent. UI hint; the server decides. */
  readonly canConfirmConsent: boolean;
}

type LoadState = 'loading' | 'ready' | 'failed';

/**
 * One import. Loads the configuration and the job (when it exists) and shows the step the job
 * is at (step = job state, PLAN §4), so an import opened again continues where it was.
 */
export const ImportTemplate = ({ canImport, jobId, importsListPath, contactGroupsPath, canConfirmConsent }: ImportProps) => {
  const { t } = useText();
  const navigate = useAdminNavigate();
  const backToList = useCallback(() => navigate(importsListPath), [navigate, importsListPath]);
  const openContactGroups = useCallback(() => navigate(contactGroupsPath), [navigate, contactGroupsPath]);

  if (!canImport || jobId === '') {
    return (
      <Box spacing={Spacing.XL}>
        <Stack spacing={Spacing.L}>
          <Callout
            type={CalloutType.FriendlyWarning}
            placement={CalloutPlacementType.OnDesk}
            dataTestId={testId(TestIds.noPermission)}
            actionButton={<Button label={t('contactsimporter.app.backtolist')} color={ButtonColor.Secondary} onClick={backToList} />}
          >
            {canImport ? t('contactsimporter.app.invalidjob') : t('contactsimporter.app.nopermission')}
          </Callout>
        </Stack>
      </Box>
    );
  }

  return <Import jobId={jobId} canConfirmConsent={canConfirmConsent} onBackToList={backToList} onOpenContactGroups={openContactGroups} />;
};

interface ImportInnerProps {
  readonly jobId: string;
  readonly canConfirmConsent: boolean;
  readonly onBackToList: () => void;
  readonly onOpenContactGroups: () => void;
}

const Import = ({ jobId, canConfirmConsent, onBackToList, onOpenContactGroups }: ImportInnerProps) => {
  const { t } = useText();
  const execute = useExecuteCommand();
  const isMounted = useIsMounted();
  const workerRef = useRef<ImportWorkerClient | null>(null);
  const [worker, setWorker] = useState<ImportWorkerClient | null>(null);

  const [loadState, setLoadState] = useState<LoadState>('loading');
  const [config, setConfig] = useState<ImportConfigResponse | null>(null);
  const [job, setJob] = useState<JobDto | null>(null);
  const [status, setStatus] = useState<StatusDto | null>(null);
  const [progress, setProgress] = useState<ProgressDto | null>(null);
  const [reports, setReports] = useState<ReportsDto | null>(null);
  /** Changes whenever the job is loaded again, so the step in view starts fresh. */
  const [epoch, setEpoch] = useState(0);
  const [reviewNotice, setReviewNotice] = useState<string | null>(null);

  const [step, setStep] = useState<Step>('file');
  const stepRef = useRef<Step>('file');
  stepRef.current = step;
  const [selected, setSelected] = useState<SelectedFile | null>(null);
  const [mapping, setMapping] = useState<MappingState | null>(null);
  const [autoStart, setAutoStart] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [serverErrors, setServerErrors] = useState<string[]>([]);

  // One worker per page visit; terminated when the page closes.
  useEffect(() => {
    const client = new ImportWorkerClient(createImportWorker());
    workerRef.current = client;
    setWorker(client);

    return () => {
      client.dispose();
      workerRef.current = null;
    };
  }, []);

  const load = useCallback(async () => {
    setLoadState('loading');
    const [loadedConfig, loadedJob] = await Promise.all([
      execute<ImportConfigResponse>(Commands.LoadConfig),
      execute<ImportJobResponse>(Commands.GetJob),
    ]);
    if (!isMounted()) {
      return;
    }
    if (!loadedConfig || !loadedJob || (loadedJob.code !== 'ok' && loadedJob.code !== 'jobNotFound')) {
      setLoadState('failed');

      return;
    }

    setConfig(loadedConfig);
    setJob(loadedJob.job);
    setStatus(loadedJob.status);
    setProgress(loadedJob.progress);
    setReports(loadedJob.reports);
    setStep(loadedJob.status ? stepForState(loadedJob.status.state, loadedJob.progress?.phase) : 'file');
    if (loadedJob.status?.state === 'Uploading') {
      // The browser cannot keep the file across a reload: it must be selected again first.
      setStep('file');
    }
    setLoadState('ready');
  }, [execute, isMounted]);

  useEffect(() => {
    void load();
  }, [load]);

  const applyJob = useCallback((next: JobView) => {
    setJob(next.job);
    setStatus(next.status);
    setProgress(next.progress);
    setReports(next.reports);
    setEpoch((current) => current + 1);
  }, []);

  /**
   * Loads the job again after it changed on the server (a phase finished, the import was
   * cancelled, another tab changed it) and shows the step its state belongs to.
   */
  const reload = useCallback(async () => {
    const next = toJobView(await execute<ImportJobResponse>(Commands.GetJob));
    if (!isMounted()) {
      return;
    }
    if (!next) {
      setLoadState('failed');

      return;
    }

    applyJob(next);
    const leftProcessing = stepRef.current === 'review' || stepRef.current === 'run';
    if (next.status.state === 'Uploaded' && next.status.error) {
      // The check or the import could not start with this configuration: the Options step says why.
      setStep('options');
    } else if (next.status.state === 'Uploaded' && leftProcessing) {
      // The job fell back to Uploaded (the check got too old to import from): check again.
      setReviewNotice(stepRef.current === 'run' ? t('contactsimporter.review.expired') : null);
      setStep('review');
    } else {
      setStep(stepForState(next.status.state, next.progress.phase));
    }
  }, [applyJob, execute, isMounted, t]);
  const reloadJob = useCallback(() => void reload(), [reload]);

  const onOptionsSaved = useCallback(
    (response: ImportJobResponse) => {
      const next = toJobView(response);
      if (!next) {
        return;
      }

      applyJob(next);
      setReviewNotice(null);
      const state = next.status.state;
      setStep(state === 'Uploaded' || state === 'Analyzing' || state === 'Analyzed' ? 'review' : stepForState(state, next.progress.phase));
    },
    [applyJob],
  );

  const onConfirmed = useCallback(
    (response: ImportJobResponse) => {
      const next = toJobView(response);
      if (next) {
        applyJob(next);
        setStep(stepForState(next.status.state, 'execute'));
      }
    },
    [applyJob],
  );

  // Fields the import cannot write (binary, references, ...) are never offered or suggested.
  const mappableFields = useMemo(() => (config ? config.fields.filter((field) => field.isSupported) : []), [config]);
  const targetFields = useMemo(() => (config ? toTargetFields(mappableFields, config.limits) : []), [config, mappableFields]);
  const saved = useMemo<SavedMapping | undefined>(
    () => (job ? { keyField: job.keyField, mapping: job.mapping, dateFormat: job.dateFormat, consentMapping: job.consentMapping } : undefined),
    [job],
  );

  const mappingFor = useCallback(
    (file: SelectedFile): MappingState => {
      const { profile } = file.profiled;

      return createMappingState(
        profile.columns.map((column) => column.name),
        targetFields,
        profile.suggestions,
        saved,
      );
    },
    [saved, targetFields],
  );

  /** The stored mapping fits the selected file, so the upload can continue without the Map step. */
  const canResumeUpload = useCallback(
    (file: SelectedFile): boolean => {
      if (!job || !saved || status?.state !== 'Uploading' || !isSameFile(job, file.file, file.profiled.fingerprint)) {
        return false;
      }

      const state = mappingFor(file);

      return validateMapping(state, saved).isValid && isSameMapping(state, saved);
    },
    [job, mappingFor, saved, status],
  );

  const openMap = useCallback(
    (file: SelectedFile) => {
      setSelected(file);
      setMapping(mappingFor(file));
      setSaveError(null);
      setServerErrors([]);
      setStep('map');
    },
    [mappingFor],
  );

  const resumeUpload = useCallback((file: SelectedFile) => {
    setSelected(file);
    setAutoStart(true);
    setStep('upload');
  }, []);

  const save = useCallback(async () => {
    if (!selected || !mapping) {
      return;
    }

    setSaving(true);
    setSaveError(null);
    setServerErrors([]);
    const response = await execute<SaveConfigResponse, SaveConfigRequest>(Commands.SaveConfig, {
      file: { name: selected.file.name, size: selected.file.size, fingerprint: selected.profiled.fingerprint },
      keyField: mapping.keyField,
      mapping: toFieldMapping(mapping),
      dateFormat: mapping.dateFormat,
      consentMapping: toConsentMapping(mapping),
    });
    if (!isMounted()) {
      return;
    }

    setSaving(false);
    if (!response) {
      setSaveError(t('contactsimporter.map.savefailed'));

      return;
    }
    if (response.code !== 'ok' || !response.job || !response.status) {
      setSaveError(response.message ?? t('contactsimporter.map.savefailed'));
      setServerErrors(response.errors ?? []);

      return;
    }

    setJob(response.job);
    setStatus(response.status);
    setAutoStart(true);
    setStep(stepForState(response.status.state));
  }, [execute, isMounted, mapping, selected, t]);

  const onStatus = useCallback(
    (next: StatusDto) => {
      setStatus(next);
      if (next.state !== 'Uploading' && next.state !== 'Uploaded') {
        // Cancelled, or changed elsewhere: the later steps need the whole job.
        void reload();
      }
    },
    [reload],
  );

  const view = useMemo<JobView | null>(
    () => (job && status && progress && reports ? { job, status, progress, reports } : null),
    [job, status, progress, reports],
  );

  if (loadState === 'loading' || !worker) {
    return (
      <Box spacing={Spacing.XL}>
        <Spinner />
        <Text>{t('contactsimporter.app.loading')}</Text>
      </Box>
    );
  }
  if (loadState === 'failed' || !config) {
    return (
      <Box spacing={Spacing.XL}>
        <Stack spacing={Spacing.L}>
          <NotificationBarAlert>{t('contactsimporter.app.loadfailed')}</NotificationBarAlert>
          <div>
            <Button label={t('contactsimporter.app.retry')} color={ButtonColor.Primary} onClick={() => void load()} />
          </div>
        </Stack>
      </Box>
    );
  }

  const state = status?.state;
  const waitsForFile = state === 'Uploading' || state === 'Configuring';

  return (
    <Box spacing={Spacing.XL}>
      <Stack spacing={Spacing.XL}>
        <StepHeader current={step} />

        {step === 'file' && (
          <FileStep
            worker={worker}
            limits={config.limits}
            targetFields={targetFields}
            selected={selected}
            initialSettings={DEFAULT_SOURCE_SETTINGS}
            resumeJob={job && waitsForFile ? job : null}
            canResumeUpload={canResumeUpload}
            onSelected={setSelected}
            onNext={openMap}
            onResumeUpload={resumeUpload}
          />
        )}

        {step === 'map' && selected && mapping && (
          <MapStep
            sample={selected.profiled.profile}
            fields={mappableFields}
            targetFields={targetFields}
            mapping={mapping}
            saved={saved}
            hasUploadedData={(status?.chunks.length ?? 0) > 0}
            fileChanged={job !== null && !isSameFile(job, selected.file, selected.profiled.fingerprint)}
            saving={saving}
            error={saveError}
            serverErrors={serverErrors}
            onChange={setMapping}
            onBack={() => setStep('file')}
            onSave={() => void save()}
          />
        )}

        {step === 'upload' && job && status && (
          <UploadStep
            worker={worker}
            execute={execute}
            limits={config.limits}
            targetFields={targetFields}
            job={job}
            status={status}
            file={selected?.file ?? null}
            settings={selected?.settings ?? DEFAULT_SOURCE_SETTINGS}
            autoStart={autoStart}
            onStatus={onStatus}
            onBackToMap={() => selected && openMap(selected)}
            onSelectFile={() => setStep('file')}
            onNext={() => setStep('options')}
          />
        )}

        {step === 'options' && job && status && (
          <OptionsStep
            key={epoch}
            execute={execute}
            job={job}
            status={status}
            fields={config.fields}
            recipientLists={config.recipientLists ?? []}
            consents={config.consents ?? []}
            onBack={() => setStep('upload')}
            onSaved={onOptionsSaved}
          />
        )}

        {step === 'review' && view && (
          <ReviewStep
            key={epoch}
            execute={execute}
            view={view}
            fields={config.fields}
            canConfirmConsent={canConfirmConsent}
            notice={reviewNotice}
            onBackToOptions={() => setStep('options')}
            onReload={reloadJob}
            onConfirmed={onConfirmed}
          />
        )}

        {step === 'run' && view && <RunStep key={epoch} execute={execute} view={view} onReload={reloadJob} />}

        {step === 'results' && view && (
          <ResultsStep
            execute={execute}
            view={view}
            fields={config.fields}
            onOpenContactGroups={onOpenContactGroups}
            onBackToList={onBackToList}
            onReload={reloadJob}
          />
        )}
      </Stack>
    </Box>
  );
};
