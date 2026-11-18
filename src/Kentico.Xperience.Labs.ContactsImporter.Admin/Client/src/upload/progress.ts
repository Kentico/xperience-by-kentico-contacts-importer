/**
 * Rate and time-left estimate from a moving window of progress samples, so the numbers do not
 * jump with every chunk (PLAN §10 "Progress UI"). Reusable for any "done of total" phase.
 */
export interface RateEstimate {
  /** Units per second over the window; 0 until two samples exist. */
  ratePerSecond: number;
  /** Seconds until `total` at the current rate; `null` while unknown. */
  etaSeconds: number | null;
}

interface Sample {
  timeMs: number;
  done: number;
}

export class RateEstimator {
  private readonly windowMs: number;
  private readonly samples: Sample[] = [];

  /** @param windowMs Age of the oldest sample used. Default 15 s. */
  constructor(windowMs = 15_000) {
    this.windowMs = windowMs;
  }

  /** Records that `done` units were finished at `timeMs`. */
  add(timeMs: number, done: number): void {
    const last = this.samples.at(-1);
    if (last && (timeMs < last.timeMs || done < last.done)) {
      // Progress went back (a new pass started): old samples say nothing about it.
      this.samples.length = 0;
    }

    this.samples.push({ timeMs, done });
    while (
      this.samples.length > 2 &&
      timeMs - this.samples[0].timeMs > this.windowMs
    ) {
      this.samples.shift();
    }
  }

  estimate(total: number): RateEstimate {
    if (this.samples.length < 2) {
      return { ratePerSecond: 0, etaSeconds: null };
    }

    const first = this.samples[0];
    const last = this.samples[this.samples.length - 1];
    const seconds = (last.timeMs - first.timeMs) / 1000;
    const ratePerSecond = seconds > 0 ? (last.done - first.done) / seconds : 0;
    if (ratePerSecond <= 0) {
      return { ratePerSecond: 0, etaSeconds: null };
    }

    return {
      ratePerSecond,
      etaSeconds: Math.max(0, (total - last.done) / ratePerSecond),
    };
  }

  reset(): void {
    this.samples.length = 0;
  }
}

/** Row total of the file estimated from the bytes read so far; exact once everything is read. */
export function estimateTotalRows(
  rowsRead: number,
  bytesRead: number,
  totalBytes: number,
): number {
  if (bytesRead <= 0 || totalBytes <= 0 || bytesRead >= totalBytes) {
    return rowsRead;
  }

  return Math.max(rowsRead, Math.round((rowsRead / bytesRead) * totalBytes));
}

/** Completed share in percent, 0-100. */
export function percent(done: number, total: number): number {
  if (!(total > 0)) {
    return 0;
  }

  return Math.min(100, Math.max(0, (done / total) * 100));
}
