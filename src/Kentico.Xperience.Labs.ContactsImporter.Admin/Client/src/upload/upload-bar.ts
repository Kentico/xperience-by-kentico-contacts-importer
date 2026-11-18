import type { JobState } from '../shared/commands';

/** State of the upload pass of this page visit. */
export type UploadPhase = 'idle' | 'running' | 'paused' | 'failed' | 'done';

export interface UploadBarInput {
  /** Phase of the pass in this tab (`idle` when none ran). */
  readonly phase: UploadPhase;
  /** State of the stored job; `null` before the first save. */
  readonly jobState: JobState | null;
  /** A failed request waits for its next attempt. */
  readonly retrying: boolean;
}

export interface UploadBarView {
  /**
   * `progress`: the bar with progress (upload not finished); `summary`: one line about the finished
   * upload, with the details (reports) on demand; `hidden`: nothing.
   */
  readonly mode: 'hidden' | 'progress' | 'summary';
  /** Something needs the user (paused, failed, connection problem): open the details, show at once. */
  readonly problem: boolean;
  /** Show the bar only when the upload is still running after a moment (small files: no flash). */
  readonly delayed: boolean;
  /** Phase to show: the stored job decides when this tab has no pass (`done` after a reload). */
  readonly phase: UploadPhase;
}

/** What the upload bar shows (D50 "Background upload"). */
export function uploadBarView({ phase, jobState, retrying }: UploadBarInput): UploadBarView {
  const uploadFinished = jobState !== null && jobState !== 'Configuring' && jobState !== 'Uploading';
  const shownPhase: UploadPhase = uploadFinished ? 'done' : phase;
  if (jobState === null || jobState === 'Configuring') {
    return { mode: 'hidden', problem: false, delayed: false, phase: shownPhase };
  }
  if (uploadFinished) {
    return { mode: 'summary', problem: false, delayed: false, phase: shownPhase };
  }

  const problem = shownPhase === 'paused' || shownPhase === 'failed' || (shownPhase === 'running' && retrying);

  return { mode: 'progress', problem, delayed: shownPhase === 'running' && !problem, phase: shownPhase };
}
