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

import type { SampleProfile, SourceFormat, TargetField } from '../import-core';
import type { JobDto, LimitsDto } from '../shared/commands';
import { useText } from '../shared/i18n';
import { OptionSelect, TestIds, Text, stringCell, testId, textColumn } from '../shared/ui';
import type { Option } from '../shared/ui';
import type { ImportWorkerClient, ProfileResult } from '../worker/client';
import { workerErrorMessage } from '../worker/messages';

import { toOpenOptions } from './model';
import type { SourceSettings } from './model';

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
  /** `false` once the upload has started: the mapping is fixed, so "Continue to mapping" is not offered (D50). */
  readonly allowMapping?: boolean;
  /** Further buttons ("Start a new import"). */
  readonly actions?: React.ReactNode;
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
  allowMapping = true,
  actions,
}: FileStepProps) => {
  const { t, m, bytes, number } = useText();
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
          setError(m(workerErrorMessage(reason, 'file')));
        }
      } finally {
        if (request === requestRef.current) {
          setReading(false);
        }
      }
    },
    [worker, targetFields, onSelected, m],
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
        setError(t('contactsimporter.file.empty'));

        return;
      }
      if (chosen.size > limits.maxFileSizeBytes) {
        requestRef.current++;
        onSelected(null);
        setError(t('contactsimporter.file.toolarge', { size: bytes(chosen.size), maxSize: bytes(limits.maxFileSizeBytes) }));

        return;
      }

      // A sheet name belongs to the previous workbook.
      const next = settings.sheet === '' ? settings : { ...settings, sheet: '' };
      setSettings(next);
      void profile(chosen, next);
    },
    [bytes, limits.maxFileSizeBytes, onSelected, profile, settings, t],
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
      <Headline size={HeadlineSize.M}>{t('contactsimporter.file.headline')}</Headline>
      <Text>{t('contactsimporter.file.intro')}</Text>

      {resumeJob && (!selected || fingerprintMatches) && (
        <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk} headline={t('contactsimporter.file.resumeheadline')}>
          {t('contactsimporter.file.resumetext', { fileName: resumeJob.file.name, size: bytes(resumeJob.file.size) })}
        </Callout>
      )}
      {resumeJob && selected && !fingerprintMatches && (
        <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk} headline={t('contactsimporter.file.resumemismatchheadline')}>
          {allowMapping
            ? t('contactsimporter.file.resumemismatchtext', { fileName: resumeJob.file.name, size: bytes(resumeJob.file.size) })
            : t('contactsimporter.file.resumemismatchlockedtext', { fileName: resumeJob.file.name, size: bytes(resumeJob.file.size) })}
        </Callout>
      )}

      <Paper>
          <div style={{ padding: Spacing.XL }}>
            <Stack spacing={Spacing.M}>
              <Row spacing={Spacing.M} alignY={undefined}>
                <Column>
                  {/* Outside the Dropzone: its CSS sets `pointer-events: none` on every div inside it, so a button there can't be clicked. */}
                  <Button
                    label={file ? t('contactsimporter.file.change') : t('contactsimporter.file.select')}
                    color={file ? ButtonColor.Secondary : ButtonColor.Primary}
                    icon="xp-arrow-up-line"
                    dataTestId={testId(TestIds.fileSelect)}
                    onClick={() => inputRef.current?.click()}
                  />
                </Column>
                <Column>
                  <Dropzone onUpload={chooseFile} onActiveChange={setDropActive}>
                    <div
                      data-testid={TestIds.fileDropzone}
                      style={{
                        padding: Spacing.S,
                        outline: dropActive ? '2px dashed currentColor' : '1px dashed transparent',
                      }}
                    >
                      <Text hint={true}>
                        {dropActive ? t('contactsimporter.file.dropactive') : t('contactsimporter.file.drophint')}
                      </Text>
                    </div>
                  </Dropzone>
                </Column>
              </Row>
              {/* Hidden native input: the button above opens it; tests set its files directly. */}
              <input
                ref={inputRef}
                type="file"
                data-testid={TestIds.fileInput}
                accept=".csv,.tsv,.tab,.txt,.ndjson,.jsonl,.json,.xlsx,.gz,.zip"
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
                    ? t(sample.complete ? 'contactsimporter.file.summaryexact' : 'contactsimporter.file.summary', {
                        fileName: file.name,
                        size: bytes(file.size),
                        rows: sample.estimatedTotalRows,
                        columns: sample.columns.length,
                      })
                    : `${file.name} · ${bytes(file.size)}`}
                </Text>
              )}
              {reading && (
                <Row spacing={Spacing.S}>
                  <Column>
                    <Spinner />
                  </Column>
                  <Column>
                    <Text>{t('contactsimporter.file.reading')}</Text>
                  </Column>
                </Row>
              )}
            </Stack>
          </div>
      </Paper>

      {error && <NotificationBarAlert>{error}</NotificationBarAlert>}

      {file && (sample || error) && <SourceSettingsForm settings={settings} sample={sample} disabled={reading} onChange={changeSettings} />}

      {sample && !hasColumns && <NotificationBarAlert>{t('contactsimporter.file.nocolumns')}</NotificationBarAlert>}
      {sample && hasColumns && !hasRows && <NotificationBarAlert>{t('contactsimporter.file.norows')}</NotificationBarAlert>}
      {sample && sample.parseErrors > 0 && (
        <NotificationBarWarning>
          {t('contactsimporter.file.parseerrors', { amount: sample.parseErrors, total: sample.rowsSampled + sample.parseErrors })}
        </NotificationBarWarning>
      )}
      {sample && sample.raggedRows > 0 && (
        <NotificationBarWarning>{t('contactsimporter.file.raggedrows', { amount: sample.raggedRows, total: sample.rowsSampled })}</NotificationBarWarning>
      )}

      {sample && hasColumns && hasRows && <SampleTable sample={sample} />}

      <Inline spacing={Spacing.M}>
        {selected && fingerprintMatches && canResumeUpload(selected) && (
            <Button
              label={t('contactsimporter.file.resumecontinue')}
              color={ButtonColor.Primary}
              dataTestId={testId(TestIds.uploadResume)}
              onClick={() => onResumeUpload(selected)}
            />
        )}
        {allowMapping && (
          <Button
            label={t('contactsimporter.file.next')}
            color={selected && fingerprintMatches && canResumeUpload(selected) ? ButtonColor.Secondary : ButtonColor.Primary}
            disabled={!selected || !hasColumns || !hasRows || reading}
            dataTestId={testId(TestIds.fileNext)}
            onClick={() => selected && onNext(selected)}
          />
        )}
        {actions}
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
  const { t } = useText();
  const format_ = settings.format !== '' ? settings.format : (sample?.format ?? 'csv');
  const formatOptions: Option[] = [
    { value: 'csv', label: t('contactsimporter.file.formatcsv') },
    { value: 'ndjson', label: t('contactsimporter.file.formatndjson') },
    { value: 'json', label: t('contactsimporter.file.formatjson') },
    { value: 'xlsx', label: t('contactsimporter.file.formatxlsx') },
  ];
  const sheets = sample?.info.sheets ?? [];
  const sheetOptions: Option[] = sheets.map((sheet) => ({ value: sheet, label: sheet }));
  const encodingOptions: Option[] = [
    {
      value: '',
      label:
        sample && settings.encoding === ''
          ? t('contactsimporter.file.encodingautodetected', { encoding: sample.info.encoding })
          : t('contactsimporter.file.encodingauto'),
    },
    ...ENCODINGS.map((encoding) => ({ value: encoding, label: encoding })),
  ];
  const detectedDelimiter = sample?.info.delimiter;
  const delimiterOptions: Option[] = [
    {
      value: '',
      label:
        detectedDelimiter && settings.delimiter === ''
          ? t('contactsimporter.file.delimiterautodetected', { delimiter: delimiterName(detectedDelimiter, t) })
          : t('contactsimporter.file.delimiterauto'),
    },
    { value: ',', label: t('contactsimporter.file.delimitercomma') },
    { value: ';', label: t('contactsimporter.file.delimitersemicolon') },
    { value: '\t', label: t('contactsimporter.file.delimitertab') },
    { value: '|', label: t('contactsimporter.file.delimiterpipe') },
  ];
  const headerOptions: Option[] = [
    { value: 'yes', label: t('contactsimporter.file.headeryes') },
    { value: 'no', label: t('contactsimporter.file.headerno') },
  ];

  return (
    <Stack spacing={Spacing.M}>
      <Headline size={HeadlineSize.S}>{t('contactsimporter.file.settings')}</Headline>
      <Row spacing={Spacing.L}>
        <Column cols={Cols.Col3}>
          <OptionSelect
            label={t('contactsimporter.file.format')}
            value={format_}
            options={formatOptions}
            disabled={disabled}
            dataTestId={TestIds.fileFormat}
            onChange={(value) => onChange({ format: toSourceFormat(value), sheet: '' })}
          />
        </Column>
        {format_ === 'xlsx' && sheets.length > 1 && (
          <Column cols={Cols.Col3}>
            <OptionSelect
              label={t('contactsimporter.file.sheet')}
              value={settings.sheet !== '' ? settings.sheet : (sample?.info.sheet ?? '')}
              options={sheetOptions}
              disabled={disabled}
              dataTestId={TestIds.fileSheet}
              onChange={(value) => onChange({ sheet: value })}
            />
          </Column>
        )}
        {format_ !== 'xlsx' && (
          <Column cols={Cols.Col3}>
            <OptionSelect
              label={t('contactsimporter.file.encoding')}
              value={settings.encoding}
              options={encodingOptions}
              disabled={disabled}
              onChange={(value) => onChange({ encoding: value })}
            />
          </Column>
        )}
        {format_ === 'csv' && (
          <Column cols={Cols.Col3}>
            <OptionSelect
              label={t('contactsimporter.file.delimiter')}
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
              label={t('contactsimporter.file.header')}
              value={settings.hasHeader ? 'yes' : 'no'}
              options={headerOptions}
              disabled={disabled}
              onChange={(value) => onChange({ hasHeader: value !== 'no' })}
            />
          </Column>
        )}
      </Row>
      {sample?.info.zipEntry && (
        <Text hint={true} data-testid={TestIds.fileZipEntry}>
          {t('contactsimporter.file.zipentry', { entry: sample.info.zipEntry })}
        </Text>
      )}
    </Stack>
  );
};

const SOURCE_FORMATS: readonly SourceFormat[] = ['csv', 'ndjson', 'json', 'xlsx'];

function toSourceFormat(value: string): SourceFormat {
  return SOURCE_FORMATS.find((format) => format === value) ?? 'csv';
}

function delimiterName(delimiter: string, t: (key: string) => string): string {
  switch (delimiter) {
    case ',':
      return t('contactsimporter.file.delimitercomma');
    case ';':
      return t('contactsimporter.file.delimitersemicolon');
    case '\t':
      return t('contactsimporter.file.delimitertab');
    case '|':
      return t('contactsimporter.file.delimiterpipe');
    default:
      return delimiter;
  }
}

function shownColumns(sample: SampleProfile): SampleProfile['columns'] {
  return sample.columns.slice(0, MAX_SAMPLE_COLUMNS);
}

const SampleTable = ({ sample }: { readonly sample: SampleProfile }) => {
  const { t } = useText();
  const columns = useMemo(
    () => [
      textColumn('row', t('contactsimporter.file.samplerownumber'), 6, 8),
      ...shownColumns(sample).map((column) => textColumn(`c${column.index}`, column.name, 12, 30)),
    ],
    [sample, t],
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
      <Headline size={HeadlineSize.S}>{t('contactsimporter.file.sample')}</Headline>
      <Table columns={columns} rows={rows} dataTestId={testId(TestIds.sampleTable)} />
    </Stack>
  );
};
