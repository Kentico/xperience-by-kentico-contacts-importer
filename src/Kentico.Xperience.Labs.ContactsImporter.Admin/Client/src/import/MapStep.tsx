import {
  Button,
  ButtonColor,
  Callout,
  CalloutPlacementType,
  CalloutType,
  Cols,
  Column,
  Divider,
  DividerOrientation,
  Headline,
  HeadlineSize,
  Inline,
  NotificationBarAlert,
  NotificationBarWarning,
  Row,
  Spacing,
  Stack,
} from '@kentico/xperience-admin-components';
import React, { useMemo } from 'react';

import type { DateFormat, SampleProfile, TargetField } from '../import-core';
import {
  consentColumnChoices,
  hasDateTarget,
  isSameMapping,
  mappedTargets,
  setColumnTarget,
  setConsentColumn,
  setDateFormat,
  setKeyField,
  validateMapping,
} from '../mapping/mapping-state';
import type { MappingProblem, MappingState, SavedMapping } from '../mapping/mapping-state';
import type { FieldDto } from '../shared/commands';
import { Strings, format } from '../shared/strings';
import { OptionSelect, TestIds, Text, testId } from '../shared/ui';
import type { Option } from '../shared/ui';

import { fieldLabel } from './model';

const S = Strings.map;

const PROBLEM_TEXT: Record<Exclude<MappingProblem, 'missingColumn'>, string> = {
  nothingMapped: S.nothingMapped,
  noKey: S.noKey,
};

export interface MapStepProps {
  readonly sample: SampleProfile;
  readonly fields: readonly FieldDto[];
  readonly targetFields: readonly TargetField[];
  readonly mapping: MappingState;
  /** Mapping stored on the server, when the job exists. */
  readonly saved: SavedMapping | undefined;
  /** The job has uploaded data that a changed mapping or another file would delete. */
  readonly hasUploadedData: boolean;
  /** The selected file is not the file the job was started with. */
  readonly fileChanged: boolean;
  readonly saving: boolean;
  readonly error: string | null;
  readonly serverErrors: readonly string[];
  readonly onChange: (mapping: MappingState) => void;
  readonly onBack: () => void;
  readonly onSave: () => void;
}

/**
 * Map step: a contact field for each file column (suggested by name), the field existing
 * contacts are matched by, and the date format. Saving creates the job on the server.
 */
export const MapStep = ({
  sample,
  fields,
  targetFields,
  mapping,
  saved,
  hasUploadedData,
  fileChanged,
  saving,
  error,
  serverErrors,
  onChange,
  onBack,
  onSave,
}: MapStepProps) => {
  const fieldOptions = useMemo<Option[]>(
    () => [{ value: '', label: S.doNotImport }, ...fields.map((field) => ({ value: field.name, label: fieldLabel(field) }))],
    [fields],
  );
  const fieldByName = useMemo(() => new Map(fields.map((field) => [field.name, field])), [fields]);
  const validation = validateMapping(mapping, saved);
  // Only fields the server accepts as the match field are offered.
  const keyOptions: Option[] = mappedTargets(mapping)
    .filter((target) => fieldByName.get(target)?.canBeKey !== false)
    .map((target) => {
      const field = fieldByName.get(target);

      return { value: target, label: field ? fieldLabel(field) : target };
    });
  const keyField = fieldByName.get(mapping.keyField);
  const keyNotAllowed = keyField !== undefined && !keyField.canBeKey;
  const dateOptions: Option[] = [
    { value: 'iso', label: S.dateIso },
    { value: 'dmy', label: S.dateDmy },
    { value: 'mdy', label: S.dateMdy },
  ];
  const mappingChanged = saved !== undefined && !isSameMapping(mapping, saved);
  const consentChoices = consentColumnChoices(mapping);
  const consentAgreedOptions: Option[] = [{ value: '', label: S.consentAgreedNone }, ...consentChoices.map((name) => ({ value: name, label: name }))];
  const consentDateOptions: Option[] = [
    { value: '', label: S.consentDateNone },
    ...consentChoices.filter((name) => name !== mapping.consent.agreed).map((name) => ({ value: name, label: name })),
  ];

  return (
    <Stack spacing={Spacing.L}>
      <Headline size={HeadlineSize.M}>{S.headline}</Headline>
      <Text>{S.intro}</Text>

      <Stack spacing={Spacing.S}>
        <Row spacing={Spacing.L}>
          <Column cols={Cols.Col3}>
            <Text strong={true}>{S.column}</Text>
          </Column>
          <Column cols={Cols.Col4}>
            <Text strong={true}>{S.examples}</Text>
          </Column>
          <Column cols={Cols.Col1}>
            <Text strong={true}>{S.filled}</Text>
          </Column>
          <Column cols={Cols.Col4}>
            <Text strong={true}>{S.target}</Text>
          </Column>
        </Row>
        <Divider orientation={DividerOrientation.Horizontal} />
        {mapping.columns.map((column, index) => {
          const profile = sample.columns[index];

          return (
            // Column names can repeat, so the position is part of the key.
            <Row key={`${index}:${column.source}`} spacing={Spacing.L}>
              <Column cols={Cols.Col3}>
                <Text style={{ paddingTop: Spacing.S }}>{column.source}</Text>
              </Column>
              <Column cols={Cols.Col4}>
                <Text hint={true} style={{ paddingTop: Spacing.S }}>
                  {(profile?.examples ?? []).map((example) => truncate(example, 40)).join(' · ')}
                </Text>
              </Column>
              <Column cols={Cols.Col1}>
                <Text style={{ paddingTop: Spacing.S }}>{profile ? `${Math.round(profile.fillRate * 100)} %` : ''}</Text>
              </Column>
              <Column cols={Cols.Col4}>
                <div data-mapping-column={column.source} aria-label={format(S.targetFor, column.source)}>
                  <OptionSelect
                    value={column.target}
                    options={fieldOptions}
                    disabled={saving}
                    dataTestId={`contacts-importer-target-${index}`}
                    onChange={(target) => onChange(setColumnTarget(mapping, index, target))}
                  />
                </div>
              </Column>
            </Row>
          );
        })}
      </Stack>

      <Row spacing={Spacing.L}>
        <Column cols={Cols.Col4}>
          <OptionSelect
            label={S.keyField}
            value={mapping.keyField}
            options={keyOptions}
            disabled={saving || keyOptions.length === 0}
            explanationText={S.keyFieldHelp}
            dataTestId={TestIds.keyField}
            onChange={(keyField) => onChange(setKeyField(mapping, keyField))}
          />
        </Column>
        {hasDateTarget(mapping, targetFields) && (
          <Column cols={Cols.Col4}>
            <OptionSelect
              label={S.dateFormat}
              value={mapping.dateFormat}
              options={dateOptions}
              disabled={saving}
              explanationText={S.dateFormatHelp}
              onChange={(dateFormat) => onChange(setDateFormat(mapping, dateFormat as DateFormat))}
            />
          </Column>
        )}
      </Row>

      <Stack spacing={Spacing.M}>
        <Headline size={HeadlineSize.S}>{S.consentHeadline}</Headline>
        <Text>{S.consentIntro}</Text>
        <Row spacing={Spacing.L}>
          <Column cols={Cols.Col4}>
            <OptionSelect
              label={S.consentAgreed}
              value={mapping.consent.agreed}
              options={consentAgreedOptions}
              disabled={saving}
              explanationText={S.consentAgreedHelp}
              dataTestId={TestIds.consentAgreed}
              onChange={(source) => onChange(setConsentColumn(mapping, 'agreed', source))}
            />
          </Column>
          {mapping.consent.agreed !== '' && (
            <Column cols={Cols.Col4}>
              <OptionSelect
                label={S.consentDate}
                value={mapping.consent.date}
                options={consentDateOptions}
                disabled={saving}
                explanationText={S.consentDateHelp}
                dataTestId={TestIds.consentDate}
                onChange={(source) => onChange(setConsentColumn(mapping, 'date', source))}
              />
            </Column>
          )}
        </Row>
      </Stack>

      {validation.problems.map((problem) => (
        <NotificationBarAlert key={problem}>
          {problem === 'missingColumn' ? format(S.missingColumn, validation.missingColumns.join(', ')) : PROBLEM_TEXT[problem]}
        </NotificationBarAlert>
      ))}
      {keyNotAllowed && <NotificationBarAlert>{format(S.keyNotAllowed, fieldLabel(keyField))}</NotificationBarAlert>}
      {validation.notices.includes('customKey') && <NotificationBarWarning>{S.customKey}</NotificationBarWarning>}
      {validation.notices.includes('emailNotMapped') && <NotificationBarWarning>{S.emailNotMapped}</NotificationBarWarning>}
      {hasUploadedData && (mappingChanged || fileChanged) && (
        <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk}>
          {fileChanged ? S.replaceUploadedFile : S.replaceUploaded}
        </Callout>
      )}
      {error && <NotificationBarAlert>{error}</NotificationBarAlert>}
      {serverErrors.map((serverError) => (
        <NotificationBarAlert key={serverError}>{serverError}</NotificationBarAlert>
      ))}

      <Inline spacing={Spacing.M}>
          <Button label={S.back} color={ButtonColor.Secondary} disabled={saving} dataTestId={testId(TestIds.mapBack)} onClick={onBack} />
          <Button
            label={saving ? S.saving : S.next}
            color={ButtonColor.Primary}
            disabled={saving || keyNotAllowed || validation.problems.some((problem) => problem !== 'missingColumn')}
            inProgress={saving}
            dataTestId={testId(TestIds.mapNext)}
            onClick={onSave}
          />
      </Inline>
    </Stack>
  );
};

function truncate(value: string, maxLength: number): string {
  return value.length > maxLength ? `${value.slice(0, maxLength - 1)}…` : value;
}
