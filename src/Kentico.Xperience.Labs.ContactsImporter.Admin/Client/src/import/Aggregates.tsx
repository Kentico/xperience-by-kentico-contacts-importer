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

/**
 * Count rows in display order. Rows marked `optional` show only when a value is not zero. Rows of a
 * `group` that are not optional show when the import uses a recipient list / records consent.
 */
const COUNT_ROWS: readonly { key: CountKey; optional?: boolean; group?: 'list' | 'consent' }[] = [
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
  { key: 'listAdded', group: 'list' },
  { key: 'listAlreadySubscribed', group: 'list', optional: true },
  { key: 'listKeptUnsubscribed', group: 'list', optional: true },
  { key: 'listResubscribed', group: 'list', optional: true },
  { key: 'listSkippedBounced', group: 'list', optional: true },
  { key: 'listSkippedNoEmail', group: 'list', optional: true },
  { key: 'bouncedHard', group: 'list', optional: true },
  { key: 'bouncedSoftOverLimit', group: 'list', optional: true },
  { key: 'bouncedSoftUnderLimit', group: 'list', optional: true },
  { key: 'consentRecorded', group: 'consent' },
  { key: 'consentAlreadyAgreed', group: 'consent', optional: true },
  { key: 'consentKeptRevoked', group: 'consent', optional: true },
  { key: 'consentReAgreed', group: 'consent', optional: true },
  { key: 'consentNotGiven', group: 'consent', optional: true },
  { key: 'consentSkippedFutureDate', group: 'consent', optional: true },
];

/** A count of the aggregates; 0 when the server did not send it. */
function countOf(aggregates: AggregatesDto | null, key: CountKey): number {
  return aggregates?.[key] ?? 0;
}

/** Contacts of the file whose email address has bounces (hard, or soft at or under the limit). */
export function bouncedContacts(aggregates: AggregatesDto): number {
  return countOf(aggregates, 'bouncedHard') + countOf(aggregates, 'bouncedSoftOverLimit') + countOf(aggregates, 'bouncedSoftUnderLimit');
}

/** Exact value of a count for tests; the visible number is formatted. */
export const Count = ({ name, value }: { readonly name: string; readonly value: number }) => (
  <span hidden={true} data-testid={`${TestIds.countPrefix}${name}`} data-count={value} />
);

export interface AggregatesTableProps {
  /** Counts of the check (dry run); `null` hides the column. */
  readonly expected: AggregatesDto | null;
  /** Counts of the import; `null` hides the column. */
  readonly actual: AggregatesDto | null;
  /** The import adds contacts to a recipient list: its main count shows also when it is zero. */
  readonly usesList?: boolean;
  /** The import records consent agreements: its main count shows also when it is zero. */
  readonly usesConsent?: boolean;
  readonly dataTestId?: string;
}

/**
 * Counts of the check and of the import next to each other. Every number comes from the server
 * aggregates; nothing is computed here.
 */
export const AggregatesTable = ({ expected, actual, usesList = false, usesConsent = false }: AggregatesTableProps) => {
  const columns = useMemo(
    () => [
      textColumn('count', S.column, 40, 70),
      ...(expected ? [textColumn('expected', S.expected, 14, 20)] : []),
      ...(actual ? [textColumn('actual', S.actual, 14, 20)] : []),
    ],
    [expected, actual],
  );

  const visible = COUNT_ROWS.filter(({ key, optional, group }) => {
    if (!optional && (group === undefined || (group === 'list' ? usesList : usesConsent))) {
      return true;
    }
    if (key === 'contactsUpdated') {
      return (expected && expected.contactsUpdated !== expected.updated) || (actual && actual.contactsUpdated !== actual.updated);
    }

    return countOf(expected, key) !== 0 || countOf(actual, key) !== 0;
  });

  const rows: TableRow[] = visible.map(({ key }) => ({
    identifier: key,
    disabled: false,
    cells: [
      stringCell('count', S[key]),
      ...(expected ? [stringCell('expected', formatNumber(countOf(expected, key)))] : []),
      ...(actual ? [stringCell('actual', formatNumber(countOf(actual, key)))] : []),
    ],
  }));

  return (
    <>
      {COUNT_ROWS.map(({ key }) => (
        <React.Fragment key={key}>
          {expected && <Count name={`expected-${key}`} value={countOf(expected, key)} />}
          {actual && <Count name={`actual-${key}`} value={countOf(actual, key)} />}
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
