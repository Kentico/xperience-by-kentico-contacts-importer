import { Icon, Spacing } from '@kentico/xperience-admin-components';
import React from 'react';

import { useText } from '../shared/i18n';
import { TestIds } from '../shared/ui';

import { STEPS } from './model';
import type { Step } from './model';

const STEP_KEYS: Record<Step, string> = {
  file: 'contactsimporter.steps.file',
  map: 'contactsimporter.steps.map',
  upload: 'contactsimporter.steps.upload',
  options: 'contactsimporter.steps.options',
  review: 'contactsimporter.steps.review',
  run: 'contactsimporter.steps.run',
  results: 'contactsimporter.steps.results',
};

export interface StepHeaderProps {
  readonly current: Step;
}

/**
 * Step header File → Map → Upload → Options → Review → Run → Results. The admin components have
 * no wizard or stepper, so this is a plain ordered list laid out as a row; the current step
 * is marked with `aria-current="step"`.
 */
export const StepHeader = ({ current }: StepHeaderProps) => {
  const { t } = useText();
  const currentIndex = STEPS.indexOf(current);

  return (
    <nav aria-label={t('contactsimporter.steps.label')} data-testid={TestIds.steps}>
      <ol style={{ display: 'flex', flexWrap: 'wrap', gap: Spacing.M, listStyle: 'none', margin: 0, padding: 0 }}>
          {STEPS.map((step, index) => {
            const isCurrent = index === currentIndex;
            const isDone = index < currentIndex;

            return (
              <li
                key={step}
                aria-current={isCurrent ? 'step' : undefined}
                data-step={step}
                data-step-state={isCurrent ? 'current' : isDone ? 'done' : 'upcoming'}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: Spacing.XS,
                  fontWeight: isCurrent ? 700 : 400,
                  color: isCurrent || isDone ? 'var(--color-text-default-on-light)' : 'var(--color-text-hint)',
                }}
              >
                {isDone ? <Icon name="xp-check-circle" /> : <span>{index + 1}.</span>}
                <span>{t(STEP_KEYS[step])}</span>
                {index < STEPS.length - 1 && <Icon name="xp-chevron-right" />}
              </li>
            );
          })}
      </ol>
    </nav>
  );
};
