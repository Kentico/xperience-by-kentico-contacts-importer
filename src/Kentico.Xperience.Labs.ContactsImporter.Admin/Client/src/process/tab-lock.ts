/**
 * One tab of a browser drives an import at a time. The driver token lives in session storage,
 * which a duplicated tab copies; without this lock two tabs with the same token would both be
 * accepted by the server as the current driver. Uses the Web Locks API; browsers without it
 * rely on the server checks alone.
 */
export interface TabLock {
  release(): void;
}

type LockManagerLike = Pick<LockManager, "request">;

/** `navigator.locks`; lib.dom types it as always present, but not every runtime has it. */
function defaultLocks(): LockManagerLike | undefined {
  return (globalThis.navigator as Partial<Navigator> | undefined)?.locks;
}

/**
 * Takes the lock when it is free; `null` when another tab holds it. With `wait`, waits until the
 * other tab releases it ("Continue here"); `null` when the wait is aborted.
 */
export async function acquireTabLock(
  name: string,
  locks: LockManagerLike | undefined = defaultLocks(),
  wait?: AbortSignal,
): Promise<TabLock | null> {
  if (!locks) {
    return { release: () => undefined };
  }

  return new Promise<TabLock | null>((resolve) => {
    const options: LockOptions = wait
      ? { signal: wait }
      : { ifAvailable: true };
    void locks
      .request(name, options, (lock) => {
        if (!lock) {
          resolve(null);

          return undefined;
        }

        // The lock is held until this promise settles.
        return new Promise<void>((release) => {
          resolve({ release });
        });
      })
      // Aborted wait: no lock. Other errors (no lock support in this context): the server checks alone.
      .catch(() => {
        resolve(wait?.aborted ? null : { release: () => undefined });
      });
  });
}
