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
  Link,
  NotificationBarAlert,
  NotificationBarInfo,
  NotificationBarWarning,
  RadioButton,
  RadioGroup,
  Spacing,
  Stack,
} from '@kentico/xperience-admin-components';
import React, { useCallback, useMemo, useState } from 'react';

import {
  droppedOptions,
  initialOptions,
  isSameOptions,
  setConsent,
  setConsentMode,
  setRecipientList,
  setSourceField,
  toOptionsContext,
  toSaveRequest,
  validateOptions,
} from '../options/options-state';
import { Commands } from '../shared/commands';
import type { ChoiceDto, ExecuteCommand, FieldDto, ImportJobResponse, JobDto, OptionsDto, StatusDto } from '../shared/commands';
import { useIsMounted } from '../shared/hooks';
import { useText } from '../shared/i18n';
import { OptionSelect, TestIds, Text, testId } from '../shared/ui';

import { fieldLabel } from './model';

const NO_FIELD = '';
const NO_CHOICE = '';

/** Xperience documentation on data protection and consent, linked from the recipient list note (PLAN §9). */
export const DATA_PROTECTION_DOCS_URL = 'https://docs.kentico.com/documentation/developers-and-admins/data-protection/gdpr-compliance';

export interface OptionsStepProps {
  readonly execute: ExecuteCommand;
  readonly job: JobDto;
  readonly status: StatusDto;
  /** Contact fields, for the caption of the match field. */
  readonly fields: readonly FieldDto[];
  /** Existing recipient lists and consents (`LOAD_CONFIG`). */
  readonly recipientLists: readonly ChoiceDto[];
  readonly consents: readonly ChoiceDto[];
  readonly onBack: () => void;
  /** The options are stored; the response holds the job as it is now. */
  readonly onSaved: (response: ImportJobResponse) => void;
}

/**
 * Options step: the contact, recipient list and consent options of PLAN §8, saved with `SAVE_OPTIONS`.
 * The recipient list and consent options start at the choice that changes nothing.
 */
export const OptionsStep = ({ execute, job, status, fields, recipientLists, consents, onBack, onSaved }: OptionsStepProps) => {
  const { t } = useText();
  const isMounted = useIsMounted();
  const context = useMemo(() => toOptionsContext(job, { recipientLists: [...recipientLists], consents: [...consents] }), [job, recipientLists, consents]);
  const [options, setOptions] = useState<OptionsDto>(() => initialOptions(job.options, context));
  const dropped = useMemo(() => droppedOptions(job.options, context), [job.options, context]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [serverErrors, setServerErrors] = useState<string[]>([]);

  const validation = useMemo(() => validateOptions(options, context), [options, context]);
  const analyzed = status.state === 'Analyzing' || status.state === 'Analyzed' || status.state === 'Confirmed' || status.state === 'Paused';
  const unchanged = isSameOptions(options, job.options, context) && !dropped.recipientList && !dropped.consent && !dropped.consentColumn;
  const listOptions = useMemo(
    () => [{ value: NO_CHOICE, label: t('contactsimporter.listoptions.listnone') }, ...recipientLists.map((list) => ({ value: String(list.id), label: list.name }))],
    [recipientLists, t],
  );
  const consentChoices = useMemo(
    () => [{ value: NO_CHOICE, label: t('contactsimporter.consentoptions.consentnone') }, ...consents.map((consent) => ({ value: String(consent.id), label: consent.name }))],
    [consents, t],
  );
  const consentColumn = job.consentMapping?.agreed ?? '';
  const consentDateColumn = job.consentMapping?.date ?? '';
  const keyField = fields.find((field) => field.name === job.keyField);
  const sourceOptions = useMemo(
    () => [{ value: NO_FIELD, label: t('contactsimporter.options.sourcefieldnone') }, ...job.sourceFields.map((field) => ({ value: field.name, label: fieldLabel(field) }))],
    [job.sourceFields, t],
  );

  const set = useCallback(<K extends keyof OptionsDto>(key: K, value: OptionsDto[K]) => {
    setOptions((current) => ({ ...current, [key]: value }));
  }, []);

  const save = useCallback(async () => {
    setSaving(true);
    setError(null);
    setServerErrors([]);
    const response = await execute<ImportJobResponse, OptionsDto>(Commands.SaveOptions, toSaveRequest(options, context));
    if (!isMounted()) {
      return;
    }

    setSaving(false);
    if (!response) {
      setError(t('contactsimporter.options.savefailed'));
    } else if (response.code !== 'ok' || !response.job || !response.status) {
      setError(response.message ?? t('contactsimporter.options.savefailed'));
      setServerErrors(response.errors ?? []);
    } else {
      onSaved(response);
    }
  }, [context, execute, isMounted, onSaved, options, t]);

  return (
    <Stack spacing={Spacing.XL}>
      <Stack spacing={Spacing.M}>
        <Headline size={HeadlineSize.M}>{t('contactsimporter.options.headline')}</Headline>
        <Text>{t('contactsimporter.options.intro')}</Text>
        {status.state === 'Uploaded' && status.error && <NotificationBarAlert>{t('contactsimporter.options.configerror', { reason: status.error })}</NotificationBarAlert>}
        {analyzed && <NotificationBarInfo>{t('contactsimporter.options.reanalyze')}</NotificationBarInfo>}
      </Stack>

      <Choice
        name="newContacts"
        label={t('contactsimporter.options.newcontacts')}
        value={options.newContacts}
        onChange={(value) => set('newContacts', value)}
        choices={[
          ['create', t('contactsimporter.options.newcontactscreate')],
          ['skip', t('contactsimporter.options.newcontactsskip')],
        ]}
      />
      <Choice
        name="existingContacts"
        label={t('contactsimporter.options.existingcontacts')}
        value={options.existingContacts}
        onChange={(value) => set('existingContacts', value)}
        choices={[
          ['update', t('contactsimporter.options.existingcontactsupdate')],
          ['skip', t('contactsimporter.options.existingcontactsskip')],
        ]}
      />
      {validation.nothingToDo && <NotificationBarAlert>{t('contactsimporter.options.nothingtodo')}</NotificationBarAlert>}

      {options.existingContacts === 'update' && (
        <>
          <Choice
            name="emptyValues"
            label={t('contactsimporter.options.emptyvalues')}
            value={options.emptyValues}
            onChange={(value) => set('emptyValues', value)}
            choices={[
              ['ignore', t('contactsimporter.options.emptyvaluesignore')],
              ['clear', t('contactsimporter.options.emptyvaluesclear')],
            ]}
          />
          {options.emptyValues === 'clear' && (
            <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk}>
              {t('contactsimporter.options.emptyvalueswarning')}
            </Callout>
          )}
          <Choice
            name="multipleMatches"
            label={t('contactsimporter.options.multiplematches')}
            value={options.multipleMatches}
            onChange={(value) => set('multipleMatches', value)}
            choices={[
              ['updateNewest', t('contactsimporter.options.multiplematchesnewest')],
              ['updateAll', t('contactsimporter.options.multiplematchesall')],
              ['skip', t('contactsimporter.options.multiplematchesskip')],
            ]}
          />
        </>
      )}

      {job.invalidEmailApplies && (
        <Choice
          name="invalidEmail"
          label={t('contactsimporter.options.invalidemail')}
          value={options.invalidEmail}
          onChange={(value) => set('invalidEmail', value)}
          choices={[
            ['skipRow', t('contactsimporter.options.invalidemailskip')],
            ['importWithoutEmail', t('contactsimporter.options.invalidemailimport')],
          ]}
        />
      )}

      <Stack spacing={Spacing.M}>
        <Headline size={HeadlineSize.S}>{t('contactsimporter.listoptions.headline')}</Headline>
        <Text>{t('contactsimporter.listoptions.intro')}</Text>
        {dropped.recipientList && <NotificationBarWarning>{t('contactsimporter.listoptions.listdropped')}</NotificationBarWarning>}
        {recipientLists.length === 0 ? (
          <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk}>
            {t('contactsimporter.listoptions.nolists')}
          </Callout>
        ) : (
          <div style={{ maxWidth: 480 }}>
            <OptionSelect
              label={t('contactsimporter.listoptions.list')}
              value={options.recipientListId === null ? NO_CHOICE : String(options.recipientListId)}
              options={listOptions}
              explanationText={t('contactsimporter.listoptions.listhelp')}
              dataTestId={TestIds.recipientList}
              onChange={(value) => setOptions((current) => setRecipientList(current, value === NO_CHOICE ? null : Number(value), context))}
            />
          </div>
        )}
        {options.recipientListId !== null && (
          <>
            <Callout
              type={CalloutType.FriendlyWarning}
              placement={CalloutPlacementType.OnDesk}
              headline={t('contactsimporter.listoptions.calloutheadline')}
              dataTestId={testId(TestIds.consentCallout)}
              actionButton={<Link href={DATA_PROTECTION_DOCS_URL} text={t('contactsimporter.listoptions.calloutlink')} target="_blank" />}
            >
              {t('contactsimporter.listoptions.callouttext')}
            </Callout>
            <Choice
              name="unsubscribed"
              label={t('contactsimporter.listoptions.unsubscribed')}
              value={options.unsubscribed}
              onChange={(value) => set('unsubscribed', value)}
              choices={[
                ['keep', t('contactsimporter.listoptions.unsubscribedkeep')],
                ['resubscribe', t('contactsimporter.listoptions.unsubscribedresubscribe')],
              ]}
            />
            {options.unsubscribed === 'resubscribe' && (
              <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk} headline={t('contactsimporter.listoptions.unsubscribedwarningheadline')}>
                {t('contactsimporter.listoptions.unsubscribedwarning')}
              </Callout>
            )}
            <Choice
              name="bouncedAddress"
              label={t('contactsimporter.listoptions.bounced')}
              value={options.bouncedAddress}
              onChange={(value) => set('bouncedAddress', value)}
              choices={[
                ['addAndReport', t('contactsimporter.listoptions.bouncedadd')],
                ['skipAndReport', t('contactsimporter.listoptions.bouncedskip')],
              ]}
            />
            <Text hint={true}>{t('contactsimporter.listoptions.bouncedhelp')}</Text>
          </>
        )}
      </Stack>

      <Stack spacing={Spacing.M}>
        <Headline size={HeadlineSize.S}>{t('contactsimporter.consentoptions.headline')}</Headline>
        <Text>{t('contactsimporter.consentoptions.intro')}</Text>
        {dropped.consent && <NotificationBarWarning>{t('contactsimporter.consentoptions.consentdropped')}</NotificationBarWarning>}
        {dropped.consentColumn && <NotificationBarWarning>{t('contactsimporter.consentoptions.columndropped')}</NotificationBarWarning>}
        {consents.length === 0 ? (
          <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk}>
            {t('contactsimporter.consentoptions.noconsents')}
          </Callout>
        ) : (
          <Choice
            name="consent"
            label={t('contactsimporter.consentoptions.mode')}
            value={options.consent}
            onChange={(value) => setOptions((current) => setConsentMode(current, value, context))}
            choices={[
              ['none', t('contactsimporter.consentoptions.modenone')],
              ['blanket', t('contactsimporter.consentoptions.modeblanket')],
              [
                'blanketWithColumnOverride',
                context.consentColumnMapped ? t('contactsimporter.consentoptions.modecolumn', { column: consentColumn }) : t('contactsimporter.consentoptions.modecolumnunavailable'),
                { disabled: !context.consentColumnMapped, caption: context.consentColumnMapped ? t('contactsimporter.consentoptions.modecolumnhelp') : t('contactsimporter.consentoptions.modecolumnunavailablehelp') },
              ],
            ]}
          />
        )}
        {options.consent !== 'none' && consents.length > 0 && (
          <>
            <div style={{ maxWidth: 480 }}>
              <OptionSelect
                label={t('contactsimporter.consentoptions.consent')}
                value={options.consentId === null ? NO_CHOICE : String(options.consentId)}
                options={consentChoices}
                explanationText={t('contactsimporter.consentoptions.consenthelp')}
                dataTestId={TestIds.consentSelect}
                onChange={(value) => setOptions((current) => setConsent(current, value === NO_CHOICE ? null : Number(value), context))}
              />
            </div>
            {validation.consentMissing && <NotificationBarAlert>{t('contactsimporter.consentoptions.consentmissing')}</NotificationBarAlert>}
            <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk} headline={t('contactsimporter.consentoptions.calloutheadline')}>
              {t('contactsimporter.consentoptions.callouttext')}
            </Callout>
            <Text hint={true}>
              {options.consent === 'blanketWithColumnOverride' && consentDateColumn !== '' ? t('contactsimporter.consentoptions.timecolumn', { column: consentDateColumn }) : t('contactsimporter.consentoptions.timeimport')}
            </Text>
            <Choice
              name="revokedConsent"
              label={t('contactsimporter.consentoptions.revoked')}
              value={options.revokedConsent}
              onChange={(value) => set('revokedConsent', value)}
              choices={[
                ['keep', t('contactsimporter.consentoptions.revokedkeep')],
                ['reAgree', t('contactsimporter.consentoptions.revokedreagree')],
              ]}
            />
            {options.revokedConsent === 'reAgree' && (
              <Callout type={CalloutType.FriendlyWarning} placement={CalloutPlacementType.OnDesk} headline={t('contactsimporter.consentoptions.revokedwarningheadline')}>
                {t('contactsimporter.consentoptions.revokedwarning')}
              </Callout>
            )}
          </>
        )}
      </Stack>

      <Stack spacing={Spacing.M}>
        <Headline size={HeadlineSize.S}>{t('contactsimporter.options.sourceheadline')}</Headline>
        <Text>{t('contactsimporter.options.sourceintro')}</Text>
        {job.sourceFields.length === 0 ? (
          <Callout type={CalloutType.QuickTip} placement={CalloutPlacementType.OnDesk}>
            {t('contactsimporter.options.nosourcefield')}
          </Callout>
        ) : (
          <div style={{ maxWidth: 480 }}>
            <OptionSelect
              label={t('contactsimporter.options.sourcefield')}
              value={options.sourceField ?? NO_FIELD}
              options={sourceOptions}
              explanationText={t('contactsimporter.options.sourcefieldhelp')}
              dataTestId={TestIds.sourceField}
              onChange={(value) => setOptions((current) => setSourceField(current, value === NO_FIELD ? null : value, context))}
            />
          </div>
        )}
        {options.sourceField !== null && (
          <>
            <div style={{ maxWidth: 480 }}>
              <Input
                label={t('contactsimporter.options.sourcevalue')}
                value={options.sourceValue ?? ''}
                invalid={validation.sourceValue !== null}
                validationMessage={
                  validation.sourceValue === 'missing'
                    ? t('contactsimporter.options.sourcevaluemissing')
                    : validation.sourceValue === 'tooLong'
                      ? t('contactsimporter.options.sourcevaluetoolong', { maxLength: validation.sourceValueMaxLength ?? 0 })
                      : undefined
                }
                dataTestId={testId(TestIds.sourceValue)}
                onChange={(event) => set('sourceValue', event.target.value)}
              />
            </div>
            <Choice
              name="sourceStamp"
              label={t('contactsimporter.options.sourcestamp')}
              value={options.sourceStamp}
              onChange={(value) => set('sourceStamp', value)}
              choices={[
                ['createdOnly', t('contactsimporter.options.sourcestampcreated')],
                ['createdAndUpdated', t('contactsimporter.options.sourcestampall')],
              ]}
            />
          </>
        )}
      </Stack>

      <Stack spacing={Spacing.M}>
        <Headline size={HeadlineSize.S}>{t('contactsimporter.options.reportheadline')}</Headline>
        <Checkbox
          label={t('contactsimporter.options.reportincludeskey', { field: keyField ? fieldLabel(keyField) : job.keyField })}
          checked={options.reportIncludesKey}
          explanationText={t('contactsimporter.options.reportincludeskeyhelp')}
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
        <Button label={t('contactsimporter.options.back')} color={ButtonColor.Secondary} disabled={saving} dataTestId={testId(TestIds.optionsBack)} onClick={onBack} />
        <Button
          label={saving ? t('contactsimporter.options.saving') : unchanged && status.state !== 'Uploaded' ? t('contactsimporter.options.next') : t('contactsimporter.options.save')}
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
  readonly choices: readonly (readonly [T, string, { readonly disabled?: boolean; readonly caption?: string }?])[];
  readonly onChange: (value: T) => void;
}

/** Admin `RadioGroup` over a list of choices. */
const Choice = <T extends string>({ name, label, value, choices, onChange }: ChoiceProps<T>) => (
  <RadioGroup name={`contacts-importer-${name}`} label={label} value={value} onChange={(selected) => onChange(selected as T)}>
    {choices.map(([choice, text, extra]) => (
      <RadioButton key={choice} value={choice} disabled={extra?.disabled} caption={extra?.caption} dataTestId={testId(`${TestIds.optionPrefix}${name}-${choice}`)}>
        {text}
      </RadioButton>
    ))}
  </RadioGroup>
);
