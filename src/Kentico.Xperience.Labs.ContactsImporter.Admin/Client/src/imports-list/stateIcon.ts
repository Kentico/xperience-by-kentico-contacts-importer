import type { IconName } from '@kentico/xperience-admin-components';

import type { JobState } from '../shared/commands';

/** Admin CSS color variables used for state icons (see the `Colors` enum of the admin components). */
export const StateIconColors = {
  neutral: 'var(--color-icon-low-emphasis)',
  info: 'var(--color-info-icon)',
  success: 'var(--color-success-icon)',
  warning: 'var(--color-warning-icon)',
  alert: 'var(--color-alert-icon)',
} as const;

export interface StateIcon {
  readonly name: IconName;
  readonly color: string;
}

const STATE_ICONS: Record<JobState, StateIcon> = {
  Configuring: { name: 'xp-cogwheel', color: StateIconColors.neutral },
  Uploading: { name: 'xp-arrow-up-circle', color: StateIconColors.info },
  Uploaded: { name: 'xp-doc', color: StateIconColors.info },
  Analyzing: { name: 'xp-magnifier', color: StateIconColors.info },
  Analyzed: { name: 'xp-checklist', color: StateIconColors.info },
  Confirmed: { name: 'xp-triangle-right', color: StateIconColors.info },
  Running: { name: 'xp-rotate-right', color: StateIconColors.info },
  Paused: { name: 'xp-pause', color: StateIconColors.warning },
  Completed: { name: 'xp-check-circle', color: StateIconColors.success },
  Failed: { name: 'xp-times-circle', color: StateIconColors.alert },
  Cancelled: { name: 'xp-ban-sign', color: StateIconColors.neutral },
};

const UNKNOWN_STATE_ICON: StateIcon = { name: 'xp-question-circle', color: StateIconColors.neutral };

/** Icon and color shown next to a job state in the imports list; unknown states get a neutral question mark. */
export function stateIcon(state: JobState | string): StateIcon {
  return Object.prototype.hasOwnProperty.call(STATE_ICONS, state) ? STATE_ICONS[state as JobState] : UNKNOWN_STATE_ICON;
}
