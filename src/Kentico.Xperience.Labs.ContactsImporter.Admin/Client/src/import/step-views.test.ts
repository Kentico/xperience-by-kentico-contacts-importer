import { describe, expect, it } from 'vitest';

import type { JobState } from '../shared/commands';

import { STEPS, stepForState, stepViews } from './model';
import type { Step, StepContext, StepState } from './model';
import { shouldAutoStart } from './useProcessLoop';

/** `state` of every step, in step order, with `*` after the clickable ones. */
function header(context: StepContext): string {
  const views = stepViews(context);

  return STEPS.map((step) => `${step}:${views[step].state}${views[step].clickable ? '*' : ''}`).join(' ');
}

describe('stepViews', () => {
  it('lets the user go back but never ahead before the upload', () => {
    expect(header({ current: 'file', jobState: null })).toBe(
      'file:current map:upcoming upload:upcoming options:upcoming review:upcoming run:upcoming results:upcoming',
    );
    expect(header({ current: 'map', jobState: null })).toBe(
      'file:done* map:current upload:upcoming options:upcoming review:upcoming run:upcoming results:upcoming',
    );
    expect(header({ current: 'map', jobState: 'Configuring' })).toBe(
      'file:done* map:current upload:upcoming options:upcoming review:upcoming run:upcoming results:upcoming',
    );
  });

  it('locks File and Map once the upload has started; Upload is done when it is complete', () => {
    expect(header({ current: 'options', jobState: 'Uploading' })).toBe(
      'file:locked map:locked upload:upcoming options:current review:upcoming run:upcoming results:upcoming',
    );
    expect(header({ current: 'options', jobState: 'Uploaded' })).toBe(
      'file:locked map:locked upload:locked options:current review:upcoming run:upcoming results:upcoming',
    );
  });

  it('shows File again only to select the file of an interrupted upload, with nothing else to open', () => {
    expect(header({ current: 'file', jobState: 'Uploading' })).toBe(
      'file:current map:locked upload:upcoming options:upcoming review:upcoming run:upcoming results:upcoming',
    );
  });

  it('opens Review once a check exists or runs, and Options from Review', () => {
    for (const jobState of ['Analyzing', 'Analyzed'] as const) {
      expect(header({ current: 'options', jobState })).toBe(
        'file:locked map:locked upload:locked options:current review:done* run:upcoming results:upcoming',
      );
      expect(header({ current: 'review', jobState })).toBe(
        'file:locked map:locked upload:locked options:done* review:current run:upcoming results:upcoming',
      );
    }
    expect(header({ current: 'options', jobState: 'Paused', phase: 'analyze' })).toBe(
      'file:locked map:locked upload:locked options:current review:done* run:upcoming results:upcoming',
    );
    // Back at Uploaded after a change of options: the file is checked again from Review.
    expect(header({ current: 'review', jobState: 'Uploaded' })).toBe(
      'file:locked map:locked upload:locked options:done* review:current run:upcoming results:upcoming',
    );
  });

  it('locks every step before Run from the confirmation on', () => {
    for (const context of [
      { current: 'run', jobState: 'Confirmed' },
      { current: 'run', jobState: 'Running' },
      { current: 'run', jobState: 'Paused', phase: 'execute' },
    ] as const) {
      expect(header(context)).toBe('file:locked map:locked upload:locked options:locked review:locked run:current results:upcoming');
    }
  });

  it('offers only Results when the import is finished', () => {
    for (const jobState of ['Completed', 'Failed', 'Cancelled'] as const) {
      expect(header({ current: 'results', jobState })).toBe(
        'file:locked map:locked upload:locked options:locked review:locked run:locked results:current',
      );
    }
  });

  it('never makes the step in view or a step that is not done clickable, for every job state', () => {
    const states: (JobState | null)[] = [null, 'Configuring', 'Uploading', 'Uploaded', 'Analyzing', 'Analyzed', 'Confirmed', 'Running', 'Paused', 'Completed', 'Failed', 'Cancelled'];
    for (const jobState of states) {
      for (const phase of ['analyze', 'execute'] as const) {
        const current: Step = jobState === null ? 'file' : stepForState(jobState, phase);
        const views = stepViews({ current, jobState, phase });
        expect(views[current]).toEqual({ state: 'current', clickable: false });
        for (const step of STEPS) {
          const { state, clickable } = views[step];
          expect(clickable).toBe(state === ('done' satisfies StepState));
        }
      }
    }
  });
});

describe('shouldAutoStart', () => {
  it('continues the check or the import by itself, unless a user paused it with Pause', () => {
    expect(shouldAutoStart(true, false)).toBe(true);
    expect(shouldAutoStart(true, true)).toBe(false);
    expect(shouldAutoStart(false, false)).toBe(false);
  });
});
