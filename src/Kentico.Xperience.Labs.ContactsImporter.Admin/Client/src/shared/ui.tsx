import { CellType, ColumnContentType, Headline, HeadlineSize, MenuItem, Paper, Select, Spacing } from '@kentico/xperience-admin-components';
import type { TableCell, TableColumn, TestId } from '@kentico/xperience-admin-components';
import React, { useEffect, useState } from 'react';

/** Test IDs of the importer UI, used by the e2e page objects (tests/e2e/admin). */
export const TestIds = {
  newImport: 'contacts-importer-new-import',
  importsTable: 'contacts-importer-imports',
  /** Imports list: second line of the File cell (size) and of the Created cell (user). */
  listFileSize: 'contacts-importer-list-file-size',
  listCreatedBy: 'contacts-importer-list-created-by',
  steps: 'contacts-importer-steps',
  fileInput: 'contacts-importer-file-input',
  fileDropzone: 'contacts-importer-file-dropzone',
  fileSelect: 'contacts-importer-file-select',
  fileSummary: 'contacts-importer-file-summary',
  fileFormat: 'contacts-importer-file-format',
  fileSheet: 'contacts-importer-file-sheet',
  fileZipEntry: 'contacts-importer-file-zip-entry',
  sampleTable: 'contacts-importer-sample',
  fileNext: 'contacts-importer-file-next',
  mapNext: 'contacts-importer-map-next',
  mapBack: 'contacts-importer-map-back',
  keyField: 'contacts-importer-key-field',
  consentAgreed: 'contacts-importer-consent-agreed',
  consentDate: 'contacts-importer-consent-date',
  recipientList: 'contacts-importer-recipient-list',
  consentSelect: 'contacts-importer-consent-select',
  consentCallout: 'contacts-importer-consent-callout',
  consentSummary: 'contacts-importer-consent-summary',
  attestationStatement: 'contacts-importer-attestation-statement',
  attestationNoPermission: 'contacts-importer-attestation-no-permission',
  attestationRecord: 'contacts-importer-attestation-record',
  uploadStatus: 'contacts-importer-upload-status',
  uploadPause: 'contacts-importer-upload-pause',
  uploadResume: 'contacts-importer-upload-resume',
  uploadBar: 'contacts-importer-upload-bar',
  uploadBarText: 'contacts-importer-upload-bar-text',
  uploadDetails: 'contacts-importer-upload-details',
  uploadDetailsToggle: 'contacts-importer-upload-details-toggle',
  startNewImport: 'contacts-importer-start-new',
  resultsDelete: 'contacts-importer-results-delete',
  /** Followed by the step name (`file`, `map`, ...): the step's button in the step header. */
  stepPrefix: 'contacts-importer-step-',
  downloadRejected: 'contacts-importer-download-rejected',
  downloadDuplicates: 'contacts-importer-download-duplicates',
  noPermission: 'contacts-importer-no-permission',
  optionsNext: 'contacts-importer-options-next',
  optionsWaiting: 'contacts-importer-options-waiting',
  /** Followed by the option name and value, e.g. `...-option-emptyValues-clear`. */
  optionPrefix: 'contacts-importer-option-',
  sourceField: 'contacts-importer-source-field',
  sourceValue: 'contacts-importer-source-value',
  reportIncludesKey: 'contacts-importer-report-includes-key',
  processStatus: 'contacts-importer-process-status',
  processPause: 'contacts-importer-process-pause',
  processContinue: 'contacts-importer-process-continue',
  processWaitingForOther: 'contacts-importer-process-waiting-for-other',
  reviewBack: 'contacts-importer-review-back',
  reviewRun: 'contacts-importer-review-run',
  /** Review: another import runs (`data-other-job-id`); Run waits for it. */
  otherImportRunning: 'contacts-importer-other-import-running',
  /** Button that opens the import whose Run blocks this one. */
  openOtherImport: 'contacts-importer-open-other-import',
  /** Review: another import's Run ended after this check (`data-last-run-ended`). */
  checkOlderThanOtherRun: 'contacts-importer-check-older-than-other-run',
  /** Review mapping card; each row has `data-mapping-column` and `data-mapping-field`. */
  reviewMapping: 'contacts-importer-review-mapping',
  reviewMappingShowAll: 'contacts-importer-review-mapping-show-all',
  /** Review examples of changes; each field has `data-sample-field` and `data-sample-kind` (cleared, changed), each row `data-sample-row`. */
  reviewSamples: 'contacts-importer-review-samples',
  reviewSamplesShowAll: 'contacts-importer-review-samples-show-all',
  confirmInput: 'contacts-importer-confirm-input',
  runCancel: 'contacts-importer-run-cancel',
  resultsStatus: 'contacts-importer-results-status',
  /** Followed by `analyze` or `execute`. */
  downloadReportPrefix: 'contacts-importer-download-report-',
  contactGroupsLink: 'contacts-importer-contact-groups',
  /** Followed by rows-read, rows-uploaded, rows-skipped or likely-duplicates. */
  countPrefix: 'contacts-importer-count-',
} as const;

/** The admin components brand their test ID type; plain strings are what ends up in `data-testid`. */
export function testId(id: string): TestId {
  return id as TestId;
}

/** Plain text column for the admin `Table` (not sortable, not searchable). */
export function textColumn(name: string, caption: string, minWidth: number, maxWidth: number): TableColumn {
  return {
    name,
    caption,
    visible: true,
    minWidth,
    maxWidth,
    contentType: ColumnContentType.Text,
    sortable: false,
    searchable: false,
  };
}

export function actionColumn(name: string, caption: string, minWidth: number, maxWidth: number): TableColumn {
  return { ...textColumn(name, caption, minWidth, maxWidth), contentType: ColumnContentType.Action };
}

/** Plain single-line string cell for the admin `Table`. */
export function stringCell(columnName: string, value: string): TableCell {
  return { type: CellType.String, columnName, value } as TableCell;
}

export interface Option {
  readonly value: string;
  readonly label: string;
  /** Second line in the menu (e.g. the database column name of a field). */
  readonly secondaryLabel?: string;
}

export interface OptionSelectProps {
  readonly label?: string;
  readonly ariaLabel?: string;
  readonly value: string;
  readonly options: readonly Option[];
  readonly onChange: (value: string) => void;
  readonly disabled?: boolean;
  readonly explanationText?: string;
  readonly dataTestId?: string;
}

/** Admin `Select` over a list of options. */
export const OptionSelect = ({ label, value, options, onChange, disabled, explanationText, dataTestId }: OptionSelectProps) => (
  <Select
    label={label}
    value={value}
    disabled={disabled}
    explanationText={explanationText}
    dataTestId={dataTestId ? testId(dataTestId) : undefined}
    onChange={(selected) => onChange(selected ?? '')}
  >
    {options.map((option) => (
      <MenuItem key={option.value} value={option.value} primaryLabel={option.label} secondaryLabel={option.secondaryLabel} />
    ))}
  </Select>
);

export interface TextProps extends React.HTMLAttributes<HTMLDivElement> {
  /** Secondary text (hints, examples). */
  readonly hint?: boolean;
  readonly strong?: boolean;
}

/**
 * Longest line of prose (intros, help texts, callout texts): longer lines across the full content width are hard to
 * read. Applies to `Text` (and so to `Section` intros and step intros) and, through `ImporterRoot`, to the explanation
 * texts of admin form fields, radio button captions and callout texts. Never to tables, card rows or form controls.
 */
export const PROSE_MAX_WIDTH = '80ch';

/**
 * The admin components take no width for their help texts, so `ImporterRoot` caps them by their CSS module class
 * names (`explanation-text___…`, `caption___…`, callout `content___…`). Matched as the start of a class name, so
 * `with-explanation-text___…` (the whole form field) is not narrowed. A rename in a later admin version only means the
 * texts are full width again.
 */
const PROSE_CSS = [
  '.contacts-importer [class^="explanation-text"]',
  '.contacts-importer [class*=" explanation-text"]',
  '.contacts-importer [class^="caption___"]',
  '.contacts-importer [class*=" caption___"]',
  '.contacts-importer [class*="callout___"] [class^="content___"]',
  '.contacts-importer [class*="callout___"] [class*=" content___"]',
].join(',\n') + ` { max-width: ${PROSE_MAX_WIDTH}; }`;

/** Root of each importer page: applies the prose width to the help texts of the admin components. */
export const ImporterRoot = ({ children }: { readonly children: React.ReactNode }) => (
  <div className="contacts-importer">
    <style>{PROSE_CSS}</style>
    {children}
  </div>
);

/**
 * Plain body text. The admin components have no text component and the admin colors unstyled
 * text red on purpose, so body text sets the admin text color tokens itself. Lines are at most
 * `PROSE_MAX_WIDTH` long (a `style` can override it).
 */
export const Text = ({ hint, strong, style, children, ...rest }: TextProps) => (
  <div
    {...rest}
    style={{
      color: hint ? 'var(--color-text-low-emphasis)' : 'var(--color-text-default-on-light)',
      fontWeight: strong ? 700 : undefined,
      lineHeight: '20px',
      overflowWrap: 'anywhere',
      maxWidth: PROSE_MAX_WIDTH,
      ...style,
    }}
  >
    {children}
  </div>
);

export interface SectionProps {
  readonly title: string;
  readonly intro?: string;
  readonly children: React.ReactNode;
}

/** A titled card that groups one part of a step. */
export const Section = ({ title, intro, children }: SectionProps) => (
  <Paper>
    <div style={{ padding: Spacing.XL }}>
      {/* Explicit gaps: the admin Stack adds no space after a Headline. */}
      <div style={{ marginBottom: Spacing.L }}>
        <Headline size={HeadlineSize.S}>{title}</Headline>
        {intro && (
          <Text hint={true} style={{ marginTop: Spacing.XS }}>
            {intro}
          </Text>
        )}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: Spacing.XL }}>{children}</div>
    </div>
  </Paper>
);

export interface ProgressTrackProps {
  /** Done, in percent (clamped to 0–100). */
  readonly completed: number;
  readonly label?: string;
}

/**
 * Progress bar with a visible track. The admin `ProgressBar` is a 2 px line without a track (made for the edge of a
 * tile), so on a card it is hard to see how much is left. Same fill (the admin product gradient), 8 px high.
 */
export const ProgressTrack = ({ completed, label }: ProgressTrackProps) => {
  const percent = Math.min(100, Math.max(0, Number.isFinite(completed) ? completed : 0));

  return (
    <div
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(percent)}
      style={{
        height: 8,
        borderRadius: 4,
        overflow: 'hidden',
        background: 'var(--color-switch-off-background, var(--color-background-disabled))',
      }}
    >
      <div style={{ width: `${percent}%`, height: '100%', borderRadius: 4, background: 'var(--gradient-product)', transition: 'width 1s ease-in-out' }} />
    </div>
  );
};

/** Max width of the importer dialogs: wider lines are hard to read (the admin Dialog fills the viewport by default). */
export const DIALOG_MAX_WIDTH = 640;

/**
 * Shows its content only after `delayMs`, with a short fade. Wraps progress views: a check or a
 * run of a small file finishes before that, so the next step appears without a flash of progress.
 */
export const DelayedReveal = ({ delayMs = 400, children }: { readonly delayMs?: number; readonly children: React.ReactNode }) => {
  const [shown, setShown] = useState(false);

  useEffect(() => {
    const timer = window.setTimeout(() => setShown(true), delayMs);

    return () => window.clearTimeout(timer);
  }, [delayMs]);

  return <div style={{ opacity: shown ? 1 : 0, transition: shown ? 'opacity 150ms ease-in' : undefined }}>{children}</div>;
};
