import {
  Button,
  ButtonColor,
  Callout,
  CalloutPlacementType,
  CalloutType,
  Cols,
  Column,
  Dropzone,
  Headline,
  HeadlineSize,
  Inline,
  NotificationBarAlert,
  NotificationBarWarning,
  Paper,
  Row,
  Spacing,
  Spinner,
  Stack,
  Table,
} from '@kentico/xperience-admin-components';
import type { TableRow } from '@kentico/xperience-admin-components';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import type { SampleProfile, TargetField } from '../import-core';
import type { JobDto, LimitsDto } from '../shared/commands';
import { formatBytes, formatNumber } from '../shared/format';
import { Strings, format } from '../shared/strings';
import { OptionSelect, TestIds, Text, stringCell, testId, textColumn } from '../shared/ui';
import type { Option } from '../shared/ui';
import type { ImportWorkerClient, ProfileResult } from '../worker/client';
import { ImportWorkerError } from '../worker/protocol';

import { toOpenOptions } from './model';
import type { SourceSettings } from './model';

const S = Strings.file;

/** Columns shown in the sample table; wider files are cut so the table stays readable. */
const MAX_SAMPLE_COLUMNS = 12;
const MAX_SAMPLE_ROWS = 10;

const ENCODINGS = ['utf-8', 'windows-1252', 'windows-1250', 'iso-8859-2', 'utf-16le', 'utf-16be'] as const;

export interface SelectedFile {
  readonly file: File;
  readonly settings: SourceSettings;
  readonly profiled: ProfileResult;
}

export interface FileStepProps {
  readonly worker: ImportWorkerClient;
  readonly limits: LimitsDto;
  readonly targetFields: readonly TargetField[];
  /** File chosen earlier in this visit (back from the Map step). */
  readonly selected: SelectedFile | null;
  readonly initialSettings: SourceSettings;
  /** Job that waits for its file (resume after reload); `null` for a new import. */
  readonly resumeJob: JobDto | null;
  /** The mapping of the resumed job can be reused as is: offer "Continue upload". */
  readonly canResumeUpload: (selected: SelectedFile) => boolean;
  readonly onSelected: (selected: SelectedFile | null) => void;
  readonly onNext: (selected: SelectedFile) => void;
  readonly onResumeUpload: (selected: SelectedFile) => void;
}

/**
 * File step: choose the file, see how it was read (format, encoding, delimiter, header) with
 * overrides, and a sample of the first rows. The file is only read in the browser.
 */
export const FileStep = ({
  worker,
  limits,
  targetFields,
  selected,
  initialSettings,
  resumeJob,
  canResumeUpload,
  onSelected,
  onNext,
  onResumeUpload,
}: FileStepProps) => {
  const inputRef = useRef<HTMLInputElement>(null);
  const requestRef = useRef(0);
  const [file, setFile] = useState<File | null>(selected?.file ?? null);
  const [settings, setSettings] = useState<SourceSettings>(selected?.settings ?? initialSettings);
  const [reading, setReading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dropActive, setDropActive] = useState(false);

  const profile = useCallback(
    async (nextFile: File, nextSettings: SourceSettings) => {
      const request = ++requestRef.current;
      setReading(true);
      setError(null);
      onSelected(null);
      try {
        const profiled = await worker.profile(nextFile, toOpenOptions(nextSettings), [...targetFields]);
        if (request === requestRef.current) {
          onSelected({ file: nextFile, settings: nextSettings, profiled });
        }
      } catch (reason) {
        if (request === requestRef.current) {
          const unsupported = reason instanceof ImportWorkerError && reason.code === 'unsupportedFormat';
          setError(unsupported ? S.unsupported : format(S.readFailed, reason instanceof Error ? reason.message : String(reason)));
        }
      } finally {
        if (request === requestRef.current) {
          setReading(false);
        }
      }
    },
    [worker, targetFields, onSelected],
  );

  useEffect(
    () => () => {
      // Ignore a profile that finishes after the step is gone.
      requestRef.current++;
    },
    [],
  );

  const chooseFile = useCallback(
    (files: FileList | null) => {
      const chosen = files?.[0];
      if (!chosen) {
        return;
      }

      setFile(chosen);
      if (chosen.size === 0) {
        requestRef.current++;
        onSelected(null);
        setError(S.empty);

        return;
      }
      if (chosen.size > limits.maxFileSizeBytes) {
        requestRef.current++;
        onSelected(null);
        setError(format(S.tooLarge, formatBytes(chosen.size), formatBytes(limits.maxFileSizeBytes)));

        return;
      }

      void profile(chosen, settings);
    },
    [limits.maxFileSizeBytes, onSelected, profile, settings],
  );

  const changeSettings = useCallback(
    (change: Partial<SourceSettings>) => {
      const next = { ...settings, ...change };
      setSettings(next);
      if (file && file.size > 0 && file.size <= limits.maxFileSizeBytes) {
        void profile(file, next);
      }
    },
    [file, limits.maxFileSizeBytes, profile, settings],
  );

  const sample = selected?.profiled.profile;
  const fingerprintMatches =
    resumeJob !== null && selected !== null && isSameFile(resumeJob, selected.file, selected.profiled.fingerprint);
  const hasColumns = sample !== undefined && sample.columns.length > 0;
  const hasRows = sample !== undefined && sample.rowsSampled > 0;

  return (
    <Stack spacing={Spacing.L}>
      <Headline size={HeadlineSize.M}>{S.headline}</Headline>
      <Text>{S.intro}</Text>

      {resumeJob && (!selected || fingerprintMatches) && (
        <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk} headline={S.resumeHeadline}>
          {format(S.resumeText, resumeJob.file.name, formatBytes(resumeJob.file.size))}
        </Callout>
      )}
      {resumeJob && selected && !fingerprintMatches && (
        <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk} headline={S.resumeMismatchHeadline}>
          {format(S.resumeMismatchText, resumeJob.file.name, formatBytes(resumeJob.file.size))}
        </Callout>
      )}

      <Dropzone onUpload={chooseFile} onActiveChange={setDropActive}>
        <Paper>
          <div style={{ padding: Spacing.XL, outline: dropActive ? '2px dashed currentColor' : undefined }}>
            <Stack spacing={Spacing.M}>
              <Row spacing={Spacing.M} alignY={undefined}>
                <Column>
                  <Button
                    label={file ? S.change : S.select}
                    color={file ? ButtonColor.Secondary : ButtonColor.Primary}
                    icon="xp-arrow-up-line"
                    onClick={() => inputRef.current?.click()}
                  />
                </Column>
                <Column>
                  <Text hint={true} style={{ paddingTop: Spacing.S }}>
                    {dropActive ? S.dropActive : S.dropHint}
                  </Text>
                </Column>
              </Row>
              {/* Hidden native input: the button above opens it; tests set its files directly. */}
              <input
                ref={inputRef}
                type="file"
                data-testid={TestIds.fileInput}
                accept=".csv,.tsv,.tab,.txt,.ndjson,.jsonl,.gz"
                style={{ display: 'none' }}
                onChange={(event) => {
                  chooseFile(event.target.files);
                  // Selecting the same file again must fire a change event too.
                  event.target.value = '';
                }}
              />
              {file && (
                <Text data-testid={TestIds.fileSummary}>
                  {sample
                    ? format(
                        sample.complete ? S.summaryExact : S.summary,
                        file.name,
                        formatBytes(file.size),
                        formatNumber(sample.estimatedTotalRows),
                        formatNumber(sample.columns.length),
                      )
                    : `${file.name} · ${formatBytes(file.size)}`}
                </Text>
              )}
              {reading && (
                <Row spacing={Spacing.S}>
                  <Column>
                    <Spinner />
                  </Column>
                  <Column>
                    <Text>{S.reading}</Text>
                  </Column>
                </Row>
              )}
            </Stack>
          </div>
        </Paper>
      </Dropzone>

      {error && <NotificationBarAlert>{error}</NotificationBarAlert>}

      {file && (sample || error) && <SourceSettingsForm settings={settings} sample={sample} disabled={reading} onChange={changeSettings} />}

      {sample && !hasColumns && <NotificationBarAlert>{S.noColumns}</NotificationBarAlert>}
      {sample && hasColumns && !hasRows && <NotificationBarAlert>{S.noRows}</NotificationBarAlert>}
      {sample && sample.parseErrors > 0 && (
        <NotificationBarWarning>
          {format(S.parseErrors, formatNumber(sample.parseErrors), formatNumber(sample.rowsSampled + sample.parseErrors))}
        </NotificationBarWarning>
      )}
      {sample && sample.raggedRows > 0 && (
        <NotificationBarWarning>{format(S.raggedRows, formatNumber(sample.raggedRows), formatNumber(sample.rowsSampled))}</NotificationBarWarning>
      )}

      {sample && hasColumns && hasRows && <SampleTable sample={sample} />}

      <Inline spacing={Spacing.M}>
        {selected && fingerprintMatches && canResumeUpload(selected) && (
            <Button
              label={S.resumeContinue}
              color={ButtonColor.Primary}
              dataTestId={testId(TestIds.uploadResume)}
              onClick={() => onResumeUpload(selected)}
            />
        )}
          <Button
            label={S.next}
            color={selected && fingerprintMatches && canResumeUpload(selected) ? ButtonColor.Secondary : ButtonColor.Primary}
            disabled={!selected || !hasColumns || !hasRows || reading}
            dataTestId={testId(TestIds.fileNext)}
            onClick={() => selected && onNext(selected)}
          />
      </Inline>
    </Stack>
  );
};

/** Same file as the one the job was started with: same size and fingerprint (name may differ). */
export function isSameFile(job: JobDto, file: Pick<File, 'size'>, fingerprint: string): boolean {
  return job.file.size === file.size && job.file.fingerprint.toLowerCase() === fingerprint.toLowerCase();
}

interface SourceSettingsFormProps {
  readonly settings: SourceSettings;
  readonly sample: SampleProfile | undefined;
  readonly disabled: boolean;
  readonly onChange: (change: Partial<SourceSettings>) => void;
}

const SourceSettingsForm = ({ settings, sample, disabled, onChange }: SourceSettingsFormProps) => {
  const format_ = settings.format !== '' ? settings.format : (sample?.format ?? 'csv');
  const formatOptions: Option[] = [
    { value: 'csv', label: S.formatCsv },
    { value: 'ndjson', label: S.formatNdjson },
  ];
  const encodingOptions: Option[] = [
    { value: '', label: sample && settings.encoding === '' ? `${S.encodingAuto} (${sample.info.encoding})` : S.encodingAuto },
    ...ENCODINGS.map((encoding) => ({ value: encoding, label: encoding })),
  ];
  const detectedDelimiter = sample?.info.delimiter;
  const delimiterOptions: Option[] = [
    {
      value: '',
      label:
        detectedDelimiter && settings.delimiter === ''
          ? `${S.delimiterAuto} (${delimiterName(detectedDelimiter)})`
          : S.delimiterAuto,
    },
    { value: ',', label: S.delimiterComma },
    { value: ';', label: S.delimiterSemicolon },
    { value: '\t', label: S.delimiterTab },
    { value: '|', label: S.delimiterPipe },
  ];
  const headerOptions: Option[] = [
    { value: 'yes', label: S.headerYes },
    { value: 'no', label: S.headerNo },
  ];

  return (
    <Stack spacing={Spacing.M}>
      <Headline size={HeadlineSize.S}>{S.settings}</Headline>
      <Row spacing={Spacing.L}>
        <Column cols={Cols.Col3}>
          <OptionSelect
            label={S.format}
            value={format_}
            options={formatOptions}
            disabled={disabled}
            onChange={(value) => onChange({ format: value === 'ndjson' ? 'ndjson' : 'csv' })}
          />
        </Column>
        <Column cols={Cols.Col3}>
          <OptionSelect
            label={S.encoding}
            value={settings.encoding}
            options={encodingOptions}
            disabled={disabled}
            onChange={(value) => onChange({ encoding: value })}
          />
        </Column>
        {format_ === 'csv' && (
          <Column cols={Cols.Col3}>
            <OptionSelect
              label={S.delimiter}
              value={settings.delimiter}
              options={delimiterOptions}
              disabled={disabled}
              onChange={(value) => onChange({ delimiter: value })}
            />
          </Column>
        )}
        {format_ === 'csv' && (
          <Column cols={Cols.Col3}>
            <OptionSelect
              label={S.header}
              value={settings.hasHeader ? 'yes' : 'no'}
              options={headerOptions}
              disabled={disabled}
              onChange={(value) => onChange({ hasHeader: value !== 'no' })}
            />
          </Column>
        )}
      </Row>
    </Stack>
  );
};

function delimiterName(delimiter: string): string {
  switch (delimiter) {
    case ',':
      return S.delimiterComma;
    case ';':
      return S.delimiterSemicolon;
    case '\t':
      return S.delimiterTab;
    case '|':
      return S.delimiterPipe;
    default:
      return delimiter;
  }
}

function shownColumns(sample: SampleProfile): SampleProfile['columns'] {
  return sample.columns.slice(0, MAX_SAMPLE_COLUMNS);
}

const SampleTable = ({ sample }: { readonly sample: SampleProfile }) => {
  const columns = useMemo(
    () => [
      textColumn('row', S.sampleRowNumber, 6, 8),
      ...shownColumns(sample).map((column) => textColumn(`c${column.index}`, column.name, 12, 30)),
    ],
    [sample],
  );
  const rows = useMemo<TableRow[]>(
    () =>
      sample.sampleRows.slice(0, MAX_SAMPLE_ROWS).map((row) => ({
        identifier: row.rowNumber,
        disabled: false,
        cells: [
          stringCell('row', String(row.rowNumber)),
          ...shownColumns(sample).map((column) => stringCell(`c${column.index}`, row.values[column.index] ?? '')),
        ],
      })),
    [sample],
  );

  return (
    <Stack spacing={Spacing.M}>
      <Headline size={HeadlineSize.S}>{S.sample}</Headline>
      <Table columns={columns} rows={rows} dataTestId={testId(TestIds.sampleTable)} />
    </Stack>
  );
};
