import { describe, expect, it } from "vitest";

import { callWithLinkedAbort } from "./abort";

/** Counts the abort listeners currently registered on a signal. */
function trackListeners(signal: AbortSignal): () => number {
  let count = 0;
  const add = signal.addEventListener.bind(signal);
  const remove = signal.removeEventListener.bind(signal);
  signal.addEventListener = ((
    type: string,
    listener: EventListener,
    options?: AddEventListenerOptions,
  ) => {
    count++;
    add(type, listener, options);
  }) as typeof signal.addEventListener;
  signal.removeEventListener = ((type: string, listener: EventListener) => {
    count--;
    remove(type, listener);
  }) as typeof signal.removeEventListener;

  return () => count;
}

describe("callWithLinkedAbort", () => {
  it("calls without a controller when there is no signal", async () => {
    let seen: AbortController | undefined = new AbortController();

    const result = await callWithLinkedAbort(undefined, (controller) => {
      seen = controller;

      return Promise.resolve("ok");
    });

    expect(result).toBe("ok");
    expect(seen).toBeUndefined();
  });

  it("aborts the call controller when the signal aborts during the call", async () => {
    const outer = new AbortController();
    let linked: AbortController | undefined;

    const call = callWithLinkedAbort(outer.signal, (controller) => {
      linked = controller;

      return new Promise<string>((resolve) =>
        controller?.signal.addEventListener("abort", () => {
          resolve("aborted");
        }),
      );
    });
    outer.abort();

    expect(await call).toBe("aborted");
    expect(linked?.signal.aborted).toBe(true);
  });

  it("passes an aborted controller when the signal is already aborted", async () => {
    const outer = new AbortController();
    outer.abort();

    const aborted = await callWithLinkedAbort(outer.signal, (controller) =>
      Promise.resolve(controller?.signal.aborted),
    );

    expect(aborted).toBe(true);
  });

  it("leaves no listener on the signal after the call, so finished requests can be freed", async () => {
    const outer = new AbortController();
    const listeners = trackListeners(outer.signal);
    const controllers: AbortController[] = [];

    for (let i = 0; i < 100; i++) {
      await callWithLinkedAbort(outer.signal, (controller) => {
        if (!controller) {
          throw new Error("No linked controller.");
        }
        controllers.push(controller);
        expect(listeners()).toBe(1);

        return Promise.resolve(i);
      });
    }

    expect(listeners()).toBe(0);
    outer.abort();
    expect(controllers.some((controller) => controller.signal.aborted)).toBe(
      false,
    );
  });

  it("removes the listener when the call fails", async () => {
    const outer = new AbortController();
    const listeners = trackListeners(outer.signal);

    await expect(
      callWithLinkedAbort(outer.signal, () =>
        Promise.reject(new Error("network")),
      ),
    ).rejects.toThrow("network");

    expect(listeners()).toBe(0);
  });
});
