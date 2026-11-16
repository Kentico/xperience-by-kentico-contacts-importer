/**
 * One tab of a browser drives an import at a time. The driver token lives in session storage,
 * which a duplicated tab copies; without this lock two tabs with the same token would both be
 * accepted by the server as the current driver. Uses the Web Locks API; browsers without it
 * rely on the server checks alone.
 */
export interface TabLock {
  release(): void;
}

type LockManagerLike = Pick<LockManager, 'request'>;

/** Takes the lock when it is free; `null` when another tab holds it. */
export async function acquireTabLock(name: string, locks: LockManagerLike | undefined = globalThis.navigator?.locks): Promise<TabLock | null> {
  if (!locks) {
    return { release: () => undefined };
  }

  return new Promise<TabLock | null>((resolve) => {
    void locks
      .request(name, { ifAvailable: true }, (lock) => {
        if (!lock) {
          resolve(null);

          return undefined;
        }

        // The lock is held until this promise settles.
        return new Promise<void>((release) => resolve({ release }));
      })
      .catch(() => resolve({ release: () => undefined }));
  });
}
