import {
  Button,
  ButtonColor,
  Callout,
  CalloutPlacementType,
  CalloutType,
  Checkbox,
  Headline,
  HeadlineSize,
  Inline,
  Input,
  NotificationBarAlert,
  NotificationBarInfo,
  RadioButton,
  RadioGroup,
  Spacing,
  Stack,
} from '@kentico/xperience-admin-components';
import React, { useCallback, useMemo, useState } from 'react';

import { initialOptions, isSameOptions, setSourceField, toSaveRequest, validateOptions } from '../options/options-state';
import { Commands } from '../shared/commands';
import type { ExecuteCommand, FieldDto, ImportJobResponse, JobDto, OptionsDto, StatusDto } from '../shared/commands';
import { useIsMounted } from '../shared/hooks';
import { Strings, format } from '../shared/strings';
import { OptionSelect, TestIds, Text, testId } from '../shared/ui';

import { fieldLabel } from './model';

const S = Strings.options;
const NO_FIELD = '';

export interface OptionsStepProps {
  readonly execute: ExecuteCommand;
  readonly job: JobDto;
  readonly status: StatusDto;
  /** Contact fields, for the caption of the match field. */
  readonly fields: readonly FieldDto[];
  readonly onBack: () => void;
  /** The options are stored; the response holds the job as it is now. */
  readonly onSaved: (response: ImportJobResponse) => void;
}

/** Options step: the contact options of PLAN §8, saved with `SAVE_OPTIONS`. */
export const OptionsStep = ({ execute, job, status, fields, onBack, onSaved }: OptionsStepProps) => {
  const isMounted = useIsMounted();
  const [options, setOptions] = useState<OptionsDto>(() => initialOptions(job.options, job));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [serverErrors, setServerErrors] = useState<string[]>([]);

  const validation = useMemo(() => validateOptions(options, job), [options, job]);
  const analyzed = status.state === 'Analyzing' || status.state === 'Analyzed' || status.state === 'Confirmed' || status.state === 'Paused';
  const unchanged = isSameOptions(options, job.options, job);
  const keyField = fields.find((field) => field.name === job.keyField);
  const sourceOptions = useMemo(
    () => [{ value: NO_FIELD, label: S.sourceFieldNone }, ...job.sourceFields.map((field) => ({ value: field.name, label: fieldLabel(field) }))],
    [job.sourceFields],
  );

  const set = useCallback(<K extends keyof OptionsDto>(key: K, value: OptionsDto[K]) => {
    setOptions((current) => ({ ...current, [key]: value }));
  }, []);

  const save = useCallback(async () => {
    setSaving(true);
    setError(null);
    setServerErrors([]);
    const response = await execute<ImportJobResponse, OptionsDto>(Commands.SaveOptions, toSaveRequest(options, job));
    if (!isMounted()) {
      return;
    }

    setSaving(false);
    if (!response) {
      setError(S.saveFailed);
    } else if (response.code !== 'ok' || !response.job || !response.status) {
      setError(response.message ?? S.saveFailed);
      setServerErrors(response.errors ?? []);
    } else {
      onSaved(response);
    }
  }, [execute, isMounted, job, onSaved, options]);

  return (
    <Stack spacing={Spacing.XL}>
      <Stack spacing={Spacing.M}>
        <Headline size={HeadlineSize.M}>{S.headline}</Headline>
        <Text>{S.intro}</Text>
        {status.state === 'Uploaded' && status.error && <NotificationBarAlert>{format(S.configError, status.error)}</NotificationBarAlert>}
        {analyzed && <NotificationBarInfo>{S.reanalyze}</NotificationBarInfo>}
      </Stack>

      <Choice
        name="newContacts"
        label={S.newContacts}
        value={options.newContacts}
        onChange={(value) => set('newContacts', value)}
        choices={[
          ['create', S.newContactsCreate],
          ['skip', S.newContactsSkip],
        ]}
      />
      <Choice
        name="existingContacts"
        label={S.existingContacts}
        value={options.existingContacts}
        onChange={(value) => set('existingContacts', value)}
        choices={[
          ['update', S.existingContactsUpdate],
          ['skip', S.existingContactsSkip],
        ]}
      />
      {validation.nothingToDo && <NotificationBarAlert>{S.nothingToDo}</NotificationBarAlert>}

      {options.existingContacts === 'update' && (
        <>
          <Choice
            name="emptyValues"
            label={S.emptyValues}
            value={options.emptyValues}
            onChange={(value) => set('emptyValues', value)}
            choices={[
              ['ignore', S.emptyValuesIgnore],
              ['clear', S.emptyValuesClear],
            ]}
          />
          {options.emptyValues === 'clear' && (
            <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk}>
              {S.emptyValuesWarning}
            </Callout>
          )}
          <Choice
            name="multipleMatches"
            label={S.multipleMatches}
            value={options.multipleMatches}
            onChange={(value) => set('multipleMatches', value)}
            choices={[
              ['updateNewest', S.multipleMatchesNewest],
              ['updateAll', S.multipleMatchesAll],
              ['skip', S.multipleMatchesSkip],
            ]}
          />
        </>
      )}

      {job.invalidEmailApplies && (
        <Choice
          name="invalidEmail"
          label={S.invalidEmail}
          value={options.invalidEmail}
          onChange={(value) => set('invalidEmail', value)}
          choices={[
            ['skipRow', S.invalidEmailSkip],
            ['importWithoutEmail', S.invalidEmailImport],
          ]}
        />
      )}

      <Stack spacing={Spacing.M}>
        <Headline size={HeadlineSize.S}>{S.sourceHeadline}</Headline>
        <Text>{S.sourceIntro}</Text>
        {job.sourceFields.length === 0 ? (
          <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk}>
            {S.noSourceField}
          </Callout>
        ) : (
          <div style={{ maxWidth: 480 }}>
            <OptionSelect
              label={S.sourceField}
              value={options.sourceField ?? NO_FIELD}
              options={sourceOptions}
              explanationText={S.sourceFieldHelp}
              dataTestId={TestIds.sourceField}
              onChange={(value) => setOptions((current) => setSourceField(current, value === NO_FIELD ? null : value, job))}
            />
          </div>
        )}
        {options.sourceField !== null && (
          <>
            <div style={{ maxWidth: 480 }}>
              <Input
                label={S.sourceValue}
                value={options.sourceValue ?? ''}
                invalid={validation.sourceValue !== null}
                validationMessage={
                  validation.sourceValue === 'missing'
                    ? S.sourceValueMissing
                    : validation.sourceValue === 'tooLong'
                      ? format(S.sourceValueTooLong, validation.sourceValueMaxLength ?? 0)
                      : undefined
                }
                dataTestId={testId(TestIds.sourceValue)}
                onChange={(event) => set('sourceValue', event.target.value)}
              />
            </div>
            <Choice
              name="sourceStamp"
              label={S.sourceStamp}
              value={options.sourceStamp}
              onChange={(value) => set('sourceStamp', value)}
              choices={[
                ['createdOnly', S.sourceStampCreated],
                ['createdAndUpdated', S.sourceStampAll],
              ]}
            />
          </>
        )}
      </Stack>

      <Stack spacing={Spacing.M}>
        <Headline size={HeadlineSize.S}>{S.reportHeadline}</Headline>
        <Checkbox
          label={format(S.reportIncludesKey, keyField ? fieldLabel(keyField) : job.keyField)}
          checked={options.reportIncludesKey}
          explanationText={S.reportIncludesKeyHelp}
          dataTestId={testId(TestIds.reportIncludesKey)}
          onChange={(_event, checked) => set('reportIncludesKey', checked)}
        />
      </Stack>

      {error && <NotificationBarAlert>{error}</NotificationBarAlert>}
      {serverErrors.length > 0 && (
        <ul style={{ margin: 0, paddingLeft: Spacing.XL, color: 'var(--color-text-default-on-light)' }}>
          {serverErrors.map((message) => (
            <li key={message}>{message}</li>
          ))}
        </ul>
      )}

      <Inline spacing={Spacing.M}>
        <Button label={S.back} color={ButtonColor.Secondary} disabled={saving} dataTestId={testId(TestIds.optionsBack)} onClick={onBack} />
        <Button
          label={saving ? S.saving : unchanged && status.state !== 'Uploaded' ? S.next : S.save}
          color={ButtonColor.Primary}
          disabled={saving || !validation.isValid}
          inProgress={saving}
          dataTestId={testId(TestIds.optionsNext)}
          onClick={() => void save()}
        />
      </Inline>
    </Stack>
  );
};

interface ChoiceProps<T extends string> {
  readonly name: string;
  readonly label: string;
  readonly value: T;
  readonly choices: readonly (readonly [T, string])[];
  readonly onChange: (value: T) => void;
}

/** Admin `RadioGroup` over a list of choices. */
const Choice = <T extends string>({ name, label, value, choices, onChange }: ChoiceProps<T>) => (
  <RadioGroup name={`contacts-importer-${name}`} label={label} value={value} onChange={(selected) => onChange(selected as T)}>
    {choices.map(([choice, text]) => (
      <RadioButton key={choice} value={choice} dataTestId={testId(`${TestIds.optionPrefix}${name}-${choice}`)}>
        {text}
      </RadioButton>
    ))}
  </RadioGroup>
);
