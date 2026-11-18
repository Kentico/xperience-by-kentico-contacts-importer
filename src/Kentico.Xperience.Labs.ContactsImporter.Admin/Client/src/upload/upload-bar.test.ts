import { describe, expect, it } from 'vitest';

import { uploadBarView } from './upload-bar';

describe('uploadBarView', () => {
  it('shows nothing before the upload starts', () => {
    expect(uploadBarView({ phase: 'idle', jobState: null, retrying: false }).mode).toBe('hidden');
    expect(uploadBarView({ phase: 'idle', jobState: 'Configuring', retrying: false }).mode).toBe('hidden');
  });

  it('shows a running upload after a delay, so a small file does not flash the bar', () => {
    expect(uploadBarView({ phase: 'running', jobState: 'Uploading', retrying: false })).toEqual({
      mode: 'progress',
      problem: false,
      delayed: true,
      phase: 'running',
    });
  });

  it('shows problems at once and opens the details', () => {
    for (const phase of ['paused', 'failed'] as const) {
      expect(uploadBarView({ phase, jobState: 'Uploading', retrying: false })).toEqual({ mode: 'progress', problem: true, delayed: false, phase });
    }
    expect(uploadBarView({ phase: 'running', jobState: 'Uploading', retrying: true })).toEqual({
      mode: 'progress',
      problem: true,
      delayed: false,
      phase: 'running',
    });
  });

  it('shows the summary once the job is uploaded, whatever this tab did', () => {
    for (const jobState of ['Uploaded', 'Analyzing', 'Analyzed', 'Paused', 'Confirmed', 'Running', 'Completed', 'Cancelled', 'Failed'] as const) {
      expect(uploadBarView({ phase: 'idle', jobState, retrying: false })).toEqual({ mode: 'summary', problem: false, delayed: false, phase: 'done' });
    }
    expect(uploadBarView({ phase: 'running', jobState: 'Uploaded', retrying: false }).phase).toBe('done');
  });

  it('keeps an idle pass of an unfinished upload visible (nothing runs: the user must continue it)', () => {
    expect(uploadBarView({ phase: 'idle', jobState: 'Uploading', retrying: false })).toEqual({
      mode: 'progress',
      problem: false,
      delayed: false,
      phase: 'idle',
    });
  });
});
