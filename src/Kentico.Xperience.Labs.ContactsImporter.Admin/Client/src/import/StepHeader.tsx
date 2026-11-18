import { Icon, Spacing } from '@kentico/xperience-admin-components';
import React from 'react';

import { useText } from '../shared/i18n';
import { TestIds } from '../shared/ui';

import { STEPS } from './model';
import type { Step, StepView } from './model';

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
  /** How each step shows and whether it can be opened (`stepViews`). */
  readonly steps: Readonly<Record<Step, StepView>>;
  readonly onSelect: (step: Step) => void;
}

const BUTTON_STYLE: React.CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: Spacing.XS,
  background: 'none',
  border: 'none',
  padding: 0,
  font: 'inherit',
  color: 'inherit',
  cursor: 'pointer',
  textDecoration: 'underline',
  textUnderlineOffset: '3px',
};

/**
 * Step header File → Map → Upload → Options → Review → Run → Results. The admin components have
 * no wizard or stepper, so this is a plain ordered list laid out as a row. The step in view has
 * `aria-current="step"`; steps that can be opened again are buttons (D50); `data-step-state` is
 * `current | done | upcoming | locked`.
 */
export const StepHeader = ({ steps, onSelect }: StepHeaderProps) => {
  const { t } = useText();

  return (
    <nav aria-label={t('contactsimporter.steps.label')} data-testid={TestIds.steps}>
      <ol style={{ display: 'flex', flexWrap: 'wrap', gap: Spacing.M, listStyle: 'none', margin: 0, padding: 0 }}>
        {STEPS.map((step, index) => {
          const { state, clickable } = steps[step];
          const isCurrent = state === 'current';
          const finished = state === 'done' || state === 'locked';
          const content = (
            <>
              {finished ? (
                <span style={{ display: 'inline-flex', color: 'var(--color-success-icon)' }}>
                  <Icon name="xp-check-circle" />
                </span>
              ) : (
                <span>{index + 1}.</span>
              )}
              <span>{t(STEP_KEYS[step])}</span>
            </>
          );

          return (
            <li
              key={step}
              aria-current={isCurrent ? 'step' : undefined}
              data-step={step}
              data-step-state={state}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: Spacing.XS,
                fontWeight: isCurrent ? 700 : 400,
                color: isCurrent || state === 'done' ? 'var(--color-text-default-on-light)' : 'var(--color-text-hint)',
              }}
            >
              {clickable ? (
                <button type="button" style={BUTTON_STYLE} data-testid={`${TestIds.stepPrefix}${step}`} onClick={() => onSelect(step)}>
                  {content}
                </button>
              ) : (
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: Spacing.XS }}>{content}</span>
              )}
              {index < STEPS.length - 1 && <Icon name="xp-chevron-right" />}
            </li>
          );
        })}
      </ol>
    </nav>
  );
};
