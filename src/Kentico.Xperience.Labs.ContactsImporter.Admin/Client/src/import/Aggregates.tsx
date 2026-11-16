import { Table } from '@kentico/xperience-admin-components';
import type { TableRow } from '@kentico/xperience-admin-components';
import React, { useMemo } from 'react';

import type { AggregatesDto, FieldDto } from '../shared/commands';
import { formatNumber } from '../shared/format';
import { Strings } from '../shared/strings';
import { TestIds, stringCell, textColumn } from '../shared/ui';

import { fieldLabel } from './model';

const S = Strings.counts;

type CountKey = Exclude<keyof AggregatesDto, 'fields'>;

/** Count rows in display order. Rows marked `optional` show only when a value is not zero. */
const COUNT_ROWS: readonly { key: CountKey; optional?: boolean }[] = [
  { key: 'rows' },
  { key: 'created' },
  { key: 'updated' },
  { key: 'contactsUpdated', optional: true },
  { key: 'unchanged' },
  { key: 'skipped' },
  { key: 'skippedNew', optional: true },
  { key: 'skippedExisting', optional: true },
  { key: 'skippedMultipleMatches', optional: true },
  { key: 'skippedInvalidEmail', optional: true },
  { key: 'skippedInvalidValue', optional: true },
  { key: 'duplicateRows', optional: true },
  { key: 'multipleMatches', optional: true },
  { key: 'importedWithoutEmail', optional: true },
];

/** Exact value of a count for tests; the visible number is formatted. */
export const Count = ({ name, value }: { readonly name: string; readonly value: number }) => (
  <span hidden={true} data-testid={`${TestIds.countPrefix}${name}`} data-count={value} />
);

export interface AggregatesTableProps {
  /** Counts of the check (dry run); `null` hides the column. */
  readonly expected: AggregatesDto | null;
  /** Counts of the import; `null` hides the column. */
  readonly actual: AggregatesDto | null;
  readonly dataTestId?: string;
}

/**
 * Counts of the check and of the import next to each other. Every number comes from the server
 * aggregates; nothing is computed here.
 */
export const AggregatesTable = ({ expected, actual }: AggregatesTableProps) => {
  const columns = useMemo(
    () => [
      textColumn('count', S.column, 40, 70),
      ...(expected ? [textColumn('expected', S.expected, 14, 20)] : []),
      ...(actual ? [textColumn('actual', S.actual, 14, 20)] : []),
    ],
    [expected, actual],
  );

  const visible = COUNT_ROWS.filter(({ key, optional }) => {
    if (!optional) {
      return true;
    }
    if (key === 'contactsUpdated') {
      return (expected && expected.contactsUpdated !== expected.updated) || (actual && actual.contactsUpdated !== actual.updated);
    }

    return (expected?.[key] ?? 0) !== 0 || (actual?.[key] ?? 0) !== 0;
  });

  const rows: TableRow[] = visible.map(({ key }) => ({
    identifier: key,
    disabled: false,
    cells: [
      stringCell('count', S[key]),
      ...(expected ? [stringCell('expected', formatNumber(expected[key]))] : []),
      ...(actual ? [stringCell('actual', formatNumber(actual[key]))] : []),
    ],
  }));

  return (
    <>
      {COUNT_ROWS.map(({ key }) => (
        <React.Fragment key={key}>
          {expected && <Count name={`expected-${key}`} value={expected[key]} />}
          {actual && <Count name={`actual-${key}`} value={actual[key]} />}
        </React.Fragment>
      ))}
      <Table columns={columns} rows={rows} />
    </>
  );
};

export interface FieldChangesTableProps {
  readonly aggregates: AggregatesDto;
  /** Contact fields, for their captions. */
  readonly fields: readonly FieldDto[];
}

/** Per contact field: how many contacts get a new value, and how many of those lose their value. */
export const FieldChangesTable = ({ aggregates, fields }: FieldChangesTableProps) => {
  const columns = useMemo(
    () => [textColumn('field', S.field, 30, 60), textColumn('changed', S.fieldChanged, 16, 24), textColumn('cleared', S.fieldCleared, 16, 24)],
    [],
  );
  const rows: TableRow[] = aggregates.fields.map((change) => {
    const field = fields.find((candidate) => candidate.name === change.field);

    return {
      identifier: change.field,
      disabled: false,
      cells: [
        stringCell('field', field ? fieldLabel(field) : change.field),
        stringCell('changed', formatNumber(change.changed)),
        stringCell('cleared', formatNumber(change.cleared)),
      ],
    };
  });

  return <Table columns={columns} rows={rows} />;
};

/** Values the import clears in existing contacts, over all fields. */
export function clearedValues(aggregates: AggregatesDto): number {
  return aggregates.fields.reduce((sum, change) => sum + change.cleared, 0);
}
