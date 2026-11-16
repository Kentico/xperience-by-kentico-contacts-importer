/**
 * Runs `call` with an `AbortController` that aborts when `signal` does, for the duration of the
 * call only.
 *
 * The admin's command executor takes a controller, while the upload code passes one long-lived
 * signal to every request. The link must be removed when the call ends: a listener left on the
 * signal keeps the finished request and its multi-megabyte body reachable until the whole upload
 * is over, so memory would grow with the file size.
 */
export async function callWithLinkedAbort<T>(
  signal: AbortSignal | undefined,
  call: (controller?: AbortController) => Promise<T>,
): Promise<T> {
  if (!signal) {
    return call();
  }

  const linked = new AbortController();
  const onAbort = () => linked.abort();
  if (signal.aborted) {
    linked.abort();
  } else {
    signal.addEventListener('abort', onAbort, { once: true });
  }

  try {
    return await call(linked);
  } finally {
    signal.removeEventListener('abort', onAbort);
  }
}
