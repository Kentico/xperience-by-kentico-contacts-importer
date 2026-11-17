import { Table } from '@kentico/xperience-admin-components';
import type { TableRow } from '@kentico/xperience-admin-components';
import React, { useMemo } from 'react';

import type { AggregatesDto, FieldDto } from '../shared/commands';
import { useText } from '../shared/i18n';
import { TestIds, stringCell, textColumn } from '../shared/ui';

import { fieldLabel } from './model';

type CountKey = Exclude<keyof AggregatesDto, 'fields'>;

/**
 * Count rows in display order. Rows marked `optional` show only when a value is not zero. Rows of a
 * `group` that are not optional show when the import uses a recipient list / records consent.
 */
const COUNT_ROWS: readonly { key: CountKey; text: string; optional?: boolean; group?: 'list' | 'consent' }[] = [
  { key: 'rows', text: 'contactsimporter.counts.rows' },
  { key: 'created', text: 'contactsimporter.counts.created' },
  { key: 'updated', text: 'contactsimporter.counts.updated' },
  { key: 'contactsUpdated', text: 'contactsimporter.counts.contactsupdated', optional: true },
  { key: 'unchanged', text: 'contactsimporter.counts.unchanged' },
  { key: 'skipped', text: 'contactsimporter.counts.skipped' },
  { key: 'skippedNew', text: 'contactsimporter.counts.skippednew', optional: true },
  { key: 'skippedExisting', text: 'contactsimporter.counts.skippedexisting', optional: true },
  { key: 'skippedMultipleMatches', text: 'contactsimporter.counts.skippedmultiplematches', optional: true },
  { key: 'skippedInvalidEmail', text: 'contactsimporter.counts.skippedinvalidemail', optional: true },
  { key: 'skippedInvalidValue', text: 'contactsimporter.counts.skippedinvalidvalue', optional: true },
  { key: 'duplicateRows', text: 'contactsimporter.counts.duplicaterows', optional: true },
  { key: 'multipleMatches', text: 'contactsimporter.counts.multiplematches', optional: true },
  { key: 'importedWithoutEmail', text: 'contactsimporter.counts.importedwithoutemail', optional: true },
  { key: 'listAdded', text: 'contactsimporter.counts.listadded', group: 'list' },
  { key: 'listAlreadySubscribed', text: 'contactsimporter.counts.listalreadysubscribed', group: 'list', optional: true },
  { key: 'listKeptUnsubscribed', text: 'contactsimporter.counts.listkeptunsubscribed', group: 'list', optional: true },
  { key: 'listResubscribed', text: 'contactsimporter.counts.listresubscribed', group: 'list', optional: true },
  { key: 'listSkippedBounced', text: 'contactsimporter.counts.listskippedbounced', group: 'list', optional: true },
  { key: 'listSkippedNoEmail', text: 'contactsimporter.counts.listskippednoemail', group: 'list', optional: true },
  { key: 'bouncedHard', text: 'contactsimporter.counts.bouncedhard', group: 'list', optional: true },
  { key: 'bouncedSoftOverLimit', text: 'contactsimporter.counts.bouncedsoftoverlimit', group: 'list', optional: true },
  { key: 'bouncedSoftUnderLimit', text: 'contactsimporter.counts.bouncedsoftunderlimit', group: 'list', optional: true },
  { key: 'consentRecorded', text: 'contactsimporter.counts.consentrecorded', group: 'consent' },
  { key: 'consentAlreadyAgreed', text: 'contactsimporter.counts.consentalreadyagreed', group: 'consent', optional: true },
  { key: 'consentKeptRevoked', text: 'contactsimporter.counts.consentkeptrevoked', group: 'consent', optional: true },
  { key: 'consentReAgreed', text: 'contactsimporter.counts.consentreagreed', group: 'consent', optional: true },
  { key: 'consentNotGiven', text: 'contactsimporter.counts.consentnotgiven', group: 'consent', optional: true },
  { key: 'consentSkippedFutureDate', text: 'contactsimporter.counts.consentskippedfuturedate', group: 'consent', optional: true },
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
  const { t, number } = useText();
  const columns = useMemo(
    () => [
      textColumn('count', t('contactsimporter.counts.column'), 40, 70),
      ...(expected ? [textColumn('expected', t('contactsimporter.counts.expected'), 14, 20)] : []),
      ...(actual ? [textColumn('actual', t('contactsimporter.counts.actual'), 14, 20)] : []),
    ],
    [expected, actual, t],
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

  const rows: TableRow[] = visible.map(({ key, text }) => ({
    identifier: key,
    disabled: false,
    cells: [
      stringCell('count', t(text)),
      ...(expected ? [stringCell('expected', number(countOf(expected, key)))] : []),
      ...(actual ? [stringCell('actual', number(countOf(actual, key)))] : []),
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
  const { t, number } = useText();
  const columns = useMemo(
    () => [
      textColumn('field', t('contactsimporter.counts.field'), 30, 60),
      textColumn('changed', t('contactsimporter.counts.fieldchanged'), 16, 24),
      textColumn('cleared', t('contactsimporter.counts.fieldcleared'), 16, 24),
    ],
    [t],
  );
  const rows: TableRow[] = aggregates.fields.map((change) => {
    const field = fields.find((candidate) => candidate.name === change.field);

    return {
      identifier: change.field,
      disabled: false,
      cells: [
        stringCell('field', field ? fieldLabel(field) : change.field),
        stringCell('changed', number(change.changed)),
        stringCell('cleared', number(change.cleared)),
      ],
    };
  });

  return <Table columns={columns} rows={rows} />;
};

/** Values the import clears in existing contacts, over all fields. */
export function clearedValues(aggregates: AggregatesDto): number {
  return aggregates.fields.reduce((sum, change) => sum + change.cleared, 0);
}
