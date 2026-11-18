import { Button, ButtonColor, Cols, Column, LayoutAlignment, Row, Spacing } from '@kentico/xperience-admin-components';
import React, { useMemo, useState } from 'react';

import type { DateFormat } from '../import-core';
import type { FieldDto, JobDto } from '../shared/commands';
import { useText } from '../shared/i18n';
import { Section, TestIds, Text, testId } from '../shared/ui';

import { canCollapseMapping, mappingSummary, visibleMappingRows } from './mapping-summary';

const DATE_FORMAT_KEYS: Record<DateFormat, string> = {
  iso: 'contactsimporter.map.dateiso',
  dmy: 'contactsimporter.map.datedmy',
  mdy: 'contactsimporter.map.datemdy',
};

export interface MappingSummaryCardProps {
  readonly job: JobDto;
  readonly fields: readonly FieldDto[];
}

/**
 * Read-only card on Review: which file column goes to which contact field, the match field, the
 * date format (when dates are read) and the consent columns. Long mappings start collapsed.
 */
export const MappingSummaryCard = ({ job, fields }: MappingSummaryCardProps) => {
  const { t } = useText();
  const [expanded, setExpanded] = useState(false);
  const summary = useMemo(() => mappingSummary(job, fields), [job, fields]);
  const rows = visibleMappingRows(summary.rows, expanded);

  return (
    <Section title={t('contactsimporter.review.mappingheadline')}>
      <div data-testid={TestIds.reviewMapping} style={{ display: 'flex', flexDirection: 'column', gap: Spacing.S }}>
        <Row spacing={Spacing.L} alignY={LayoutAlignment.End}>
          <Column cols={Cols.Col4}>
            <Text strong={true}>{t('contactsimporter.map.column')}</Text>
          </Column>
          <Column cols={Cols.Col8}>
            <Text strong={true}>{t('contactsimporter.map.target')}</Text>
          </Column>
        </Row>
        {rows.map((row, index) => (
          // Column names can repeat, so the position is part of the key.
          <div key={`${index}:${row.column}`} style={ROW_STYLE} data-mapping-column={row.column} data-mapping-field={row.field} data-mapping-key={row.isKey}>
            <Row spacing={Spacing.L} alignY={LayoutAlignment.Center}>
              <Column cols={Cols.Col4}>
                <Text>{row.column}</Text>
              </Column>
              <Column cols={Cols.Col8}>
                <Text strong={true}>
                  <span data-mapping-label="">{row.label}</span>
                  {row.isKey && <span style={KEY_MARK_STYLE}>{t('contactsimporter.review.mappingkeymark')}</span>}
                </Text>
                <Text hint={true}>{row.field}</Text>
              </Column>
            </Row>
          </div>
        ))}
        {canCollapseMapping(summary.rows) && (
          <div>
            <Button
              label={expanded ? t('contactsimporter.review.mappingshowfewer') : t('contactsimporter.review.mappingshowall', { amount: summary.rows.length })}
              color={ButtonColor.Tertiary}
              dataTestId={testId(TestIds.reviewMappingShowAll)}
              onClick={() => setExpanded((value) => !value)}
            />
          </div>
        )}
      </div>
      {(summary.key || summary.dateFormat || summary.consentAgreed) && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: Spacing.XS }}>
          {summary.key && <Text>{t('contactsimporter.review.mappingkey', { field: summary.key.label })}</Text>}
          {summary.dateFormat && <Text>{t('contactsimporter.review.mappingdateformat', { format: t(DATE_FORMAT_KEYS[summary.dateFormat]) })}</Text>}
          {summary.consentAgreed && <Text>{t('contactsimporter.review.mappingconsentagreed', { column: summary.consentAgreed })}</Text>}
          {summary.consentDate && <Text>{t('contactsimporter.review.mappingconsentdate', { column: summary.consentDate })}</Text>}
        </div>
      )}
    </Section>
  );
};

const ROW_STYLE: React.CSSProperties = {
  borderTop: '1px solid var(--color-border-default, rgba(0, 0, 0, 0.08))',
  paddingTop: Spacing.S,
};

const KEY_MARK_STYLE: React.CSSProperties = {
  marginLeft: Spacing.S,
  padding: '0 6px',
  borderRadius: 4,
  fontSize: 12,
  fontWeight: 400,
  color: 'var(--color-text-low-emphasis)',
  border: '1px solid var(--color-border-default, rgba(0, 0, 0, 0.16))',
};
