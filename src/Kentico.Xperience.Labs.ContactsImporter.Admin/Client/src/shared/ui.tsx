import { CellType, ColumnContentType, MenuItem, Select } from '@kentico/xperience-admin-components';
import type { TableCell, TableColumn, TestId } from '@kentico/xperience-admin-components';
import React from 'react';

/** Test IDs of the importer UI, used by the e2e page objects (tests/e2e/admin). */
export const TestIds = {
  newImport: 'contacts-importer-new-import',
  importsTable: 'contacts-importer-imports',
  steps: 'contacts-importer-steps',
  fileInput: 'contacts-importer-file-input',
  fileSummary: 'contacts-importer-file-summary',
  sampleTable: 'contacts-importer-sample',
  fileNext: 'contacts-importer-file-next',
  mapNext: 'contacts-importer-map-next',
  mapBack: 'contacts-importer-map-back',
  keyField: 'contacts-importer-key-field',
  uploadStatus: 'contacts-importer-upload-status',
  uploadPause: 'contacts-importer-upload-pause',
  uploadResume: 'contacts-importer-upload-resume',
  uploadNext: 'contacts-importer-upload-next',
  cancelImport: 'contacts-importer-cancel',
  downloadRejected: 'contacts-importer-download-rejected',
  downloadDuplicates: 'contacts-importer-download-duplicates',
  noPermission: 'contacts-importer-no-permission',
  optionsNext: 'contacts-importer-options-next',
  optionsBack: 'contacts-importer-options-back',
  /** Followed by the option name and value, e.g. `...-option-emptyValues-clear`. */
  optionPrefix: 'contacts-importer-option-',
  sourceField: 'contacts-importer-source-field',
  sourceValue: 'contacts-importer-source-value',
  reportIncludesKey: 'contacts-importer-report-includes-key',
  processStatus: 'contacts-importer-process-status',
  processPause: 'contacts-importer-process-pause',
  processContinue: 'contacts-importer-process-continue',
  reviewBack: 'contacts-importer-review-back',
  reviewRun: 'contacts-importer-review-run',
  confirmInput: 'contacts-importer-confirm-input',
  runCancel: 'contacts-importer-run-cancel',
  resultsStatus: 'contacts-importer-results-status',
  /** Followed by `analyze` or `execute`. */
  downloadReportPrefix: 'contacts-importer-download-report-',
  deleteFiles: 'contacts-importer-delete-files',
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
      <MenuItem key={option.value} value={option.value} primaryLabel={option.label} />
    ))}
  </Select>
);

export interface TextProps extends React.HTMLAttributes<HTMLDivElement> {
  /** Secondary text (hints, examples). */
  readonly hint?: boolean;
  readonly strong?: boolean;
}

/**
 * Plain body text. The admin components have no text component and the admin colors unstyled
 * text red on purpose, so body text sets the admin text color tokens itself.
 */
export const Text = ({ hint, strong, style, children, ...rest }: TextProps) => (
  <div
    {...rest}
    style={{
      color: hint ? 'var(--color-text-low-emphasis)' : 'var(--color-text-default-on-light)',
      fontWeight: strong ? 700 : undefined,
      lineHeight: '20px',
      overflowWrap: 'anywhere',
      ...style,
    }}
  >
    {children}
  </div>
);
